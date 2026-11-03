using Hihooi.Core.Configuration;
using Hihooi.Core.Consistency;
using Hihooi.Core.TransactionManager;
using Hihooi.Tests.Fakes;

namespace Hihooi.Tests.Integration;

public class ReplicaOfflineBehaviorTests
{
    [Fact]
    public async Task Marking_extension_offline_routes_gssi_reads_to_primary()
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
        await using var session = tm.OpenSession(ConsistencyLevel.Gssi);
        await session.ExecuteNonQueryAsync("INSERT INTO Items (Id, Name) VALUES (10, 'x')");
        await Task.Delay(100);
        tm.MarkExtensionOffline("ext1");

        var rows = await session.QueryAsync("SELECT Name FROM Items WHERE Id = 10");
        Assert.Single(rows);
        Assert.True(tm.Metrics.PrimaryReads + tm.Metrics.PrimaryFallbacks >= 1);
    }
}
