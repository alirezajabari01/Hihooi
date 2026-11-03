using Hihooi.Core.Buffer;
using Hihooi.Core.Configuration;
using Hihooi.Core.TransactionManager;
using Hihooi.Tests.Fakes;

namespace Hihooi.Tests.Concurrency;

public class ConcurrentTsIdTests
{
    [Fact]
    public async Task Many_parallel_commits_produce_unique_monotonic_tsids()
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

        var tasks = Enumerable.Range(1, 50).Select(async i =>
        {
            await using var s = tm.OpenSession();
            await s.ExecuteNonQueryAsync($"INSERT INTO T{i % 5} (Id, Name) VALUES ({i}, 'x')");
        });
        await Task.WhenAll(tasks);

        var ids = buffer.SnapshotLog().Select(x => x.TsId).OrderBy(x => x).ToList();
        Assert.Equal(50, ids.Count);
        Assert.Equal(ids.Distinct().Count(), ids.Count);
        Assert.Equal(1, ids.First());
        Assert.Equal(50, ids.Last());
    }
}
