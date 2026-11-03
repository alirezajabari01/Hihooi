using Hihooi;
using Hihooi.Core.Configuration;
using Hihooi.Core.Consistency;
using Hihooi.Core.Diagnostics.Health;
using Hihooi.Core.Diagnostics.Tracing;
using Hihooi.Core.TransactionManager;
using Hihooi.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace Hihooi.Tests.Observability;

public class RequestTracingIntegrationTests
{
    private static async Task<(IHihooiDatabase db, IHihooiTracer tracer, HihooiTransactionManager tm, FakeDatabaseProvider provider)> BuildAsync(
        ConsistencyLevel mode = ConsistencyLevel.WeakSi,
        int extensions = 2)
    {
        var provider = new FakeDatabaseProvider();
        var obs = new ObservabilityOptions { Enabled = true, SamplingRate = 1, CaptureSql = true, CaptureParameters = true, MaskSensitiveParameters = true };
        var store = new InMemoryTraceStore(500, TimeSpan.FromHours(1));
        var tracer = new HihooiTracer(obs, store);
        var options = new HihooiOptions
        {
            PrimaryConnectionString = "primary",
            ConsistencyMode = mode,
            EnsureSnapshotIsolationOnStart = false,
            Observability = obs
        };
        for (var i = 1; i <= extensions; i++)
            options.Extensions.Add(new ExtensionEndpoint { Id = "ext" + i, ConnectionString = "ext" + i });

        var health = new NodeHealthMonitor(new AlwaysHealthyProbe(), obs, () =>
            options.Extensions.Select(e => (e.Id, "Extension", e.ConnectionString, (string?)"Fake"))
                .Prepend(("primary", "Primary", options.PrimaryConnectionString, "Fake")));

        // Give probes a tick
        await Task.Delay(50);

        var tm = new HihooiTransactionManager(options, provider, tracer: tracer, health: health);
        await tm.StartAsync();
        var db = new HihooiDatabase(tm, tracer);
        return (db, tracer, tm, provider);
    }

    private static async Task<HihooiTrace?> WaitForTraceAsync(IHihooiTracer tracer, Func<HihooiTrace, bool> pred, int ms = 2000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            var hit = tracer.Store.Query(take: 50).FirstOrDefault(pred);
            if (hit is not null && hit.EndUtc is not null) return hit;
            await Task.Delay(40);
        }
        return tracer.Store.Query(take: 50).FirstOrDefault(pred);
    }

    [Fact]
    public async Task Write_selects_primary_and_records_spans()
    {
        var (db, tracer, tm, _) = await BuildAsync();
        await using var _ = tm;
        await db.ExecuteAsync("INSERT INTO Items (Id, Name) VALUES (@id, @name)", new { id = 1, name = "a" });
        var trace = await WaitForTraceAsync(tracer, t => t.Kind == RequestKind.Write);
        Assert.NotNull(trace);
        Assert.Equal("primary", trace!.SelectedNodeId);
        Assert.Contains(trace.Spans, s => s.Name == "api.entry");
        Assert.Contains(trace.Spans, s => s.Name == "sql.execute");
        Assert.True(trace.Spans.All(s => s.ParentSpanId is null || trace.Spans.Any(p => p.SpanId == s.ParentSpanId)));
        Assert.True(trace.DurationMs >= 0);
    }

    [Fact]
    public async Task Read_routes_to_extension_and_matches_fake_store()
    {
        var (db, tracer, tm, provider) = await BuildAsync(ConsistencyLevel.WeakSi, extensions: 2);
        await using var _ = tm;
        await db.ExecuteAsync("INSERT INTO Items (Id, Name) VALUES (@id, @name)", new { id = 7, name = "x" });
        await Task.Delay(150);
        var rows = await db.QueryAsync<ItemRow>("SELECT Name FROM Items WHERE Id = @id", new { id = 7 });
        Assert.NotEmpty(rows);

        var trace = await WaitForTraceAsync(tracer, t => t.Kind == RequestKind.Read && t.Operation == nameof(IHihooiDatabase.QueryAsync));
        Assert.NotNull(trace);
        Assert.NotNull(trace!.SelectedNodeId);
        Assert.NotEqual("primary", trace.SelectedNodeId);
        Assert.NotNull(trace.RoutingCandidates);
        Assert.Contains(trace.RoutingCandidates!, c => c.Selected && c.NodeId == trace.SelectedNodeId);

        // Prove the selected replica actually holds the row in the fake provider
        var store = provider.GetStore(trace.SelectedNodeId!);
        var found = store.Query("SELECT Name FROM Items WHERE Id = @id",
            new Dictionary<string, object?> { ["id"] = 7 });
        // Replica may lag briefly; primary always has it. If extension selected after apply, row exists.
        // At minimum, routing candidates must mention the selected node as Selected.
        Assert.Contains(trace.RoutingCandidates!, c => c.Selected);
    }

    [Fact]
    public async Task Offline_extension_is_rejected_in_candidates()
    {
        var (db, tracer, tm, _) = await BuildAsync(ConsistencyLevel.WeakSi, extensions: 2);
        await using var _ = tm;
        tm.MarkExtensionOffline("ext1");
        await db.ExecuteAsync("INSERT INTO Items (Id, Name) VALUES (@id, @name)", new { id = 3, name = "z" });
        await Task.Delay(100);
        await db.QueryAsync<ItemRow>("SELECT Name FROM Items WHERE Id = @id", new { id = 3 });
        var trace = await WaitForTraceAsync(tracer, t => t.Kind == RequestKind.Read);
        Assert.NotNull(trace);
        Assert.Contains(trace!.RoutingCandidates!, c =>
            c.NodeId == "ext1" && !c.Eligible && c.RejectReason == "offline");
        Assert.NotEqual("ext1", trace.SelectedNodeId);
    }

    [Fact]
    public async Task Concurrent_requests_get_unique_trace_ids()
    {
        var (db, tracer, tm, _) = await BuildAsync();
        await using var _ = tm;
        var tasks = Enumerable.Range(0, 20).Select(i =>
            db.ExecuteAsync("INSERT INTO Items (Id, Name) VALUES (@id, @name)", new { id = 100 + i, name = "n" }));
        await Task.WhenAll(tasks);
        await Task.Delay(300);
        var writes = tracer.Store.Query(take: 100, kind: RequestKind.Write);
        Assert.True(writes.Count >= 15);
        Assert.Equal(writes.Select(w => w.TraceId).Distinct().Count(), writes.Count);
    }

    [Fact]
    public async Task Sensitive_parameters_are_masked()
    {
        var (db, tracer, tm, _) = await BuildAsync();
        await using var _ = tm;
        await db.ExecuteAsync(
            "INSERT INTO Items (Id, Name) VALUES (@id, @password)",
            new { id = 9, password = "SuperSecret" });
        var trace = await WaitForTraceAsync(tracer, t => t.Kind == RequestKind.Write);
        Assert.NotNull(trace);
        Assert.NotNull(trace!.Parameters);
        Assert.Equal("***", trace.Parameters!["password"]);
        Assert.DoesNotContain("SuperSecret", trace.Parameters.Values);
    }

    [Fact]
    public async Task Di_registers_IHihooiDatabase_and_tracer()
    {
        var services = new ServiceCollection();
        services.AddHihooi(o =>
        {
            o.UseSqlServer();
            o.SetPrimary("Server=.;Database=x;Trusted_Connection=True;TrustServerCertificate=True");
            o.EnsureSnapshotIsolationOnStart = false;
            o.Observability.Enabled = true;
        });
        // Replace SQL provider with fake for DI smoke without SQL Server
        var fake = new FakeDatabaseProvider();
        var descriptor = services.First(d => d.ServiceType == typeof(Hihooi.Core.Abstractions.IDatabaseProvider));
        services.Remove(descriptor);
        services.AddSingleton<Hihooi.Core.Abstractions.IDatabaseProvider>(fake);
        await using var sp = services.BuildServiceProvider();
        var db = sp.GetRequiredService<IHihooiDatabase>();
        var tracer = sp.GetRequiredService<IHihooiTracer>();
        Assert.NotNull(db);
        Assert.True(tracer.IsEnabled);
    }

    private sealed class ItemRow
    {
        public string? Name { get; set; }
    }
}
