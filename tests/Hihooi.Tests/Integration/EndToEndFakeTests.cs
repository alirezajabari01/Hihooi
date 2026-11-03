using Hihooi.Core.Buffer;
using Hihooi.Core.Configuration;
using Hihooi.Core.Consistency;
using Hihooi.Core.TransactionManager;
using Hihooi.Tests.Fakes;

namespace Hihooi.Tests.Integration;

public class EndToEndFakeTests
{
    [Fact]
    public async Task Write_replicates_to_extension_and_routing_uses_extension_when_caught_up()
    {
        var provider = new FakeDatabaseProvider();
        var buffer = new InMemoryTransactionsBuffer();
        var options = new HihooiOptions
        {
            PrimaryConnectionString = "primary",
            Extensions =
            {
                new ExtensionEndpoint { Id = "ext1", ConnectionString = "ext1" }
            },
            EnsureSnapshotIsolationOnStart = false,
            DefaultConsistencyLevel = ConsistencyLevel.Gssi
        };

        await using var tm = new HihooiTransactionManager(options, provider, buffer: buffer);
        await tm.StartAsync();

        await using var session = tm.OpenSession();
        await session.BeginTransactionAsync();
        await session.ExecuteNonQueryAsync("INSERT INTO Items (Id, Name) VALUES (1, 'a')");
        await session.CommitAsync();

        await WaitForAsync(() => tm.Extensions.Snapshot().First().AppliedTsId >= 1, TimeSpan.FromSeconds(3));

        var primary = provider.GetStore("primary").Query("SELECT * FROM Items WHERE Id = 1", null);
        var ext = provider.GetStore("ext1").Query("SELECT * FROM Items WHERE Id = 1", null);
        Assert.Single(primary);
        Assert.Single(ext);

        // After replication, GSSI read of Items should be routable (required TSID=1, ext applied>=1)
        var rows = await session.QueryAsync("SELECT Name FROM Items WHERE Id = 1");
        Assert.Single(rows);
    }

    [Fact]
    public async Task Rollback_is_replicated_as_rollback_completion()
    {
        var provider = new FakeDatabaseProvider();
        var buffer = new InMemoryTransactionsBuffer();
        var options = new HihooiOptions
        {
            PrimaryConnectionString = "primary",
            Extensions = { new ExtensionEndpoint { Id = "ext1", ConnectionString = "ext1" } },
            EnsureSnapshotIsolationOnStart = false
        };
        await using var tm = new HihooiTransactionManager(options, provider, buffer: buffer);
        await tm.StartAsync();

        await using var session = tm.OpenSession();
        await session.BeginTransactionAsync();
        await session.ExecuteNonQueryAsync("INSERT INTO Items (Id, Name) VALUES (2, 'b')");
        await session.RollbackAsync();

        await WaitForAsync(() => tm.LatestTsId >= 1, TimeSpan.FromSeconds(2));
        var log = buffer.SnapshotLog();
        Assert.Contains(log, s => s.Completion == Core.Model.CompletionOperation.Rollback);
    }

    [Fact]
    public async Task WeakSi_can_route_before_extension_catches_up()
    {
        var provider = new FakeDatabaseProvider();
        // No extractors consuming slowly — use registry with applied 0
        var options = new HihooiOptions
        {
            PrimaryConnectionString = "primary",
            Extensions = { new ExtensionEndpoint { Id = "ext1", ConnectionString = "ext1" } },
            EnsureSnapshotIsolationOnStart = false,
            DefaultConsistencyLevel = ConsistencyLevel.WeakSi
        };
        await using var tm = new HihooiTransactionManager(options, provider);
        await tm.StartAsync();

        // Force lag: mark applied 0 after a write by not waiting
        await using var session = tm.OpenSession(ConsistencyLevel.WeakSi);
        await session.ExecuteNonQueryAsync("INSERT INTO Items (Id, Name) VALUES (3, 'c')");
        // Weak SI should not throw when reading
        var rows = await session.QueryAsync("SELECT * FROM Items");
        Assert.NotNull(rows);
    }

    [Fact]
    public async Task OneCopySerializability_waits_for_extensions()
    {
        var provider = new FakeDatabaseProvider();
        var options = new HihooiOptions
        {
            PrimaryConnectionString = "primary",
            Extensions = { new ExtensionEndpoint { Id = "ext1", ConnectionString = "ext1" } },
            EnsureSnapshotIsolationOnStart = false,
            DefaultConsistencyLevel = ConsistencyLevel.OneCopySerializability
        };
        await using var tm = new HihooiTransactionManager(options, provider);
        await tm.StartAsync();

        await using var session = tm.OpenSession(ConsistencyLevel.OneCopySerializability);
        await session.ExecuteNonQueryAsync("INSERT INTO Items (Id, Name) VALUES (4, 'd')");
        Assert.True(tm.Extensions.Snapshot().First().AppliedTsId >= 1);
    }

    [Fact]
    public async Task Extension_offline_falls_back_to_primary_for_gssi()
    {
        var provider = new FakeDatabaseProvider();
        var options = new HihooiOptions
        {
            PrimaryConnectionString = "primary",
            Extensions = { new ExtensionEndpoint { Id = "ext1", ConnectionString = "ext1" } },
            EnsureSnapshotIsolationOnStart = false
        };
        await using var tm = new HihooiTransactionManager(options, provider);
        await tm.StartAsync();
        tm.MarkExtensionOffline("ext1");

        await using var session = tm.OpenSession();
        await session.ExecuteNonQueryAsync("INSERT INTO Items (Id, Name) VALUES (5, 'e')");
        var rows = await session.QueryAsync("SELECT * FROM Items WHERE Id = 5");
        Assert.Single(rows);
    }

    [Fact]
    public async Task Concurrent_independent_writes_get_monotonic_tsids()
    {
        var provider = new FakeDatabaseProvider();
        var options = new HihooiOptions
        {
            PrimaryConnectionString = "primary",
            Extensions = { new ExtensionEndpoint { Id = "ext1", ConnectionString = "ext1" } },
            EnsureSnapshotIsolationOnStart = false
        };
        await using var tm = new HihooiTransactionManager(options, provider);
        await tm.StartAsync();

        async Task Write(int id, string table)
        {
            await using var s = tm.OpenSession();
            await s.ExecuteNonQueryAsync($"INSERT INTO {table} (Id, Name) VALUES ({id}, 'x')");
        }

        await Task.WhenAll(Write(1, "A"), Write(2, "B"), Write(3, "C"));
        Assert.True(tm.LatestTsId >= 3);
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        Assert.True(condition(), "Condition not met before timeout");
    }
}
