using Hihooi.Core.Configuration;
using Hihooi.Core.Consistency;
using Hihooi.Core.TransactionManager;
using Hihooi.Tests.Fakes;

namespace Hihooi.Tests.Integration;

public class OneCopySerializabilityOfflineTests
{
    [Fact]
    public async Task OneSr_commit_fails_when_extension_offline()
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
        tm.MarkExtensionOffline("ext1");

        await using var session = tm.OpenSession(ConsistencyLevel.OneCopySerializability);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.ExecuteNonQueryAsync("INSERT INTO Items (Id, Name) VALUES (9, 'z')"));
    }
}
