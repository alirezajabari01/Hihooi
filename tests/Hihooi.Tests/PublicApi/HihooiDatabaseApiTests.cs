using Hihooi;
using Hihooi.Core.Abstractions;
using Hihooi.Core.Buffer;
using Hihooi.Core.Configuration;
using Hihooi.Core.Consistency;
using Hihooi.Core.Diagnostics;
using Hihooi.Core.Parsing;
using Hihooi.Core.TransactionManager;
using Hihooi.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace Hihooi.Tests.PublicApi;

public class HihooiDatabaseApiTests
{
    private sealed class Product
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public int Stock { get; set; }
        public bool IsActive { get; set; }
    }

    private static (ServiceProvider Sp, FakeDatabaseProvider Provider) BuildFakeDi(
        ConsistencyLevel consistency = ConsistencyLevel.Gssi,
        bool withReplica = true)
    {
        var provider = new FakeDatabaseProvider();
        var services = new ServiceCollection();
        var options = new HihooiOptions
        {
            ProviderKind = HihooiDatabaseProviderKind.SqlServer,
            PrimaryConnectionString = "primary",
            ConsistencyMode = consistency,
            EnsureSnapshotIsolationOnStart = false
        };
        if (withReplica)
            options.Extensions.Add(new ExtensionEndpoint { Id = "ext1", ConnectionString = "ext1" });

        services.AddSingleton(options);
        services.AddSingleton<HihooiMetrics>();
        services.AddSingleton<HihooiEventStore>();
        services.AddSingleton<ISqlAnalyzer, LightweightSqlAnalyzer>();
        services.AddSingleton<ITransactionsBuffer, InMemoryTransactionsBuffer>();
        services.AddSingleton<IDatabaseProvider>(provider);
        services.AddSingleton<HihooiTransactionManager>();
        services.AddSingleton<HihooiClient>();
        services.AddSingleton<HihooiDatabase>();
        services.AddSingleton<IHihooiDatabase>(sp => sp.GetRequiredService<HihooiDatabase>());
        return (services.BuildServiceProvider(), provider);
    }

    [Fact]
    public async Task Di_resolves_IHihooiDatabase()
    {
        await using var sp = BuildFakeDi().Sp;
        var db = sp.GetRequiredService<IHihooiDatabase>();
        Assert.NotNull(db);
        Assert.IsType<HihooiDatabase>(db);
    }

    [Fact]
    public async Task Execute_insert_update_delete_and_query_map()
    {
        await using var sp = BuildFakeDi().Sp;
        var db = sp.GetRequiredService<IHihooiDatabase>();

        var inserted = await db.ExecuteAsync(
            "INSERT INTO Products (Id, Name, Stock, IsActive) VALUES (@id, @name, @stock, @active)",
            new { id = 15, name = "Widget", stock = 10, active = 1 });
        Assert.Equal(1, inserted);

        var updated = await db.ExecuteAsync(
            "UPDATE Products SET Stock = Stock + @amount WHERE Id = @id",
            new { amount = 1, id = 15 });
        Assert.Equal(1, updated);

        var products = await db.QueryAsync<Product>(
            "SELECT Id, Name, Stock, IsActive FROM Products WHERE Id = @id",
            new { id = 15 });
        Assert.Single(products);
        Assert.Equal("Widget", products[0].Name);
        Assert.Equal(11, products[0].Stock);

        var one = await db.QueryFirstOrDefaultAsync<Product>(
            "SELECT Id, Name, Stock, IsActive FROM Products WHERE Id = @id",
            new { id = 15 });
        Assert.NotNull(one);
        Assert.Equal(15, one!.Id);

        var single = await db.QuerySingleAsync<Product>(
            "SELECT Id, Name, Stock, IsActive FROM Products WHERE Id = @id",
            new { id = 15 });
        Assert.Equal(15, single.Id);

        var deleted = await db.ExecuteAsync(
            "DELETE FROM Products WHERE Id = @id",
            new { id = 15 });
        Assert.Equal(1, deleted);

        var missing = await db.QueryFirstOrDefaultAsync<Product>(
            "SELECT Id, Name, Stock, IsActive FROM Products WHERE Id = @id",
            new { id = 15 });
        Assert.Null(missing);
    }

    [Fact]
    public async Task Concurrent_requests_do_not_throw()
    {
        await using var sp = BuildFakeDi().Sp;
        var db = sp.GetRequiredService<IHihooiDatabase>();

        var tasks = Enumerable.Range(1, 40).Select(async i =>
        {
            await db.ExecuteAsync(
                "INSERT INTO Products (Id, Name, Stock, IsActive) VALUES (@id, @name, @stock, @active)",
                new { id = i, name = "p" + i, stock = i, active = 1 });
        });
        await Task.WhenAll(tasks);

        var all = await db.QueryAsync<Product>("SELECT Id, Name, Stock, IsActive FROM Products");
        Assert.Equal(40, all.Count);
    }

    [Fact]
    public async Task Writes_replicate_to_extension_store()
    {
        var (sp, provider) = BuildFakeDi();
        await using (sp)
        {
            var db = sp.GetRequiredService<IHihooiDatabase>();
            await db.ExecuteAsync(
                "INSERT INTO Products (Id, Name, Stock, IsActive) VALUES (@id, @name, @stock, @active)",
                new { id = 7, name = "rep", stock = 1, active = 1 });

            await WaitAsync(() =>
            {
                var rows = provider.GetStore("ext1").Query(
                    "SELECT Id, Name FROM Products WHERE Id = 7", null);
                return rows.Count == 1;
            }, TimeSpan.FromSeconds(3));
        }
    }

    [Fact]
    public async Task Gssi_offline_replica_still_serves_reads_from_primary()
    {
        await using var sp = BuildFakeDi().Sp;
        var db = sp.GetRequiredService<IHihooiDatabase>();
        var client = sp.GetRequiredService<HihooiClient>();
        await db.ExecuteAsync(
            "INSERT INTO Products (Id, Name, Stock, IsActive) VALUES (@id, @name, @stock, @active)",
            new { id = 3, name = "x", stock = 1, active = 1 });
        client.MarkExtensionOffline("ext1");

        var rows = await db.QueryAsync<Product>(
            "SELECT Id, Name, Stock, IsActive FROM Products WHERE Id = @id",
            new { id = 3 });
        Assert.Single(rows);
        Assert.True(client.Metrics.PrimaryReads + client.Metrics.PrimaryFallbacks >= 1);
    }

    [Fact]
    public async Task Events_and_metrics_record_commits()
    {
        await using var sp = BuildFakeDi().Sp;
        var db = sp.GetRequiredService<IHihooiDatabase>();
        var client = sp.GetRequiredService<HihooiClient>();
        await db.ExecuteAsync(
            "INSERT INTO Products (Id, Name, Stock, IsActive) VALUES (@id, @name, @stock, @active)",
            new { id = 1, name = "m", stock = 1, active = 1 });
        Assert.True(client.Metrics.Commits >= 1);
        Assert.Contains(client.GetRecentEvents(), e => e.EventType == HihooiEventType.TransactionCommitted);
    }

    [Fact]
    public void Fluent_registration_requires_primary()
    {
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() =>
            services.AddHihooi(o => o.UseSqlServer()));
    }

    [Fact]
    public void UsePostgreSQL_throws_not_supported()
    {
        var o = new HihooiOptions();
        Assert.Throws<NotSupportedException>(() => o.UsePostgreSQL());
    }

    [Fact]
    public void Parameter_binder_maps_anonymous_object()
    {
        var bound = SqlParameterBinder.Bind(new { amount = 1, id = 15 });
        Assert.Equal(1, bound["amount"]);
        Assert.Equal(15, bound["id"]);
    }

    [Fact]
    public void Row_mapper_maps_product()
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Id"] = 15,
            ["Name"] = "Widget",
            ["Stock"] = 10,
            ["IsActive"] = true
        };
        var p = Hihooi.Core.Mapping.RowMapper.Map<Product>(row);
        Assert.Equal(15, p.Id);
        Assert.Equal("Widget", p.Name);
        Assert.Equal(10, p.Stock);
        Assert.True(p.IsActive);
    }

    private static async Task WaitAsync(Func<bool> cond, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (cond()) return;
            await Task.Delay(20);
        }
        Assert.True(cond());
    }
}
