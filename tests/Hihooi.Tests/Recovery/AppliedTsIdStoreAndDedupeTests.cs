using Hihooi.Core.Buffer;
using Hihooi.Core.Configuration;
using Hihooi.Core.Model;
using Hihooi.Core.Recovery;
using Hihooi.Core.TransactionManager;
using Hihooi.Tests.Fakes;

namespace Hihooi.Tests.Recovery;

public class AppliedTsIdStoreAndDedupeTests
{
    [Fact]
    public async Task Applied_store_roundtrips_watermark()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hihooi-applied-" + Guid.NewGuid().ToString("N"));
        var store = new FileAppliedTsIdStore(dir);
        await store.SaveAsync("ext1", 42);
        Assert.Equal(42, await store.LoadAsync("ext1"));
        try { Directory.Delete(dir, true); } catch { /* ignore */ }
    }

    [Fact]
    public async Task After_restart_extractor_skips_already_applied_tsids()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hihooi-rec-" + Guid.NewGuid().ToString("N"));
        var provider = new FakeDatabaseProvider();
        var buffer = new InMemoryTransactionsBuffer();
        var options = new HihooiOptions
        {
            PrimaryConnectionString = "primary",
            Extensions = { new ExtensionEndpoint { Id = "ext1", ConnectionString = "ext1" } },
            EnsureSnapshotIsolationOnStart = false,
            RecoveryJournalDirectory = dir
        };

        await using (var tm = new HihooiTransactionManager(options, provider, buffer: buffer))
        {
            await tm.StartAsync();
            await using var s = tm.OpenSession();
            await s.ExecuteNonQueryAsync("INSERT INTO Items (Id, Name) VALUES (1, 'a')");
            await WaitAsync(() => tm.Extensions.Snapshot().First().AppliedTsId >= 1, TimeSpan.FromSeconds(3));
        }

        // Simulate restart with journal republish of TSID 1 while watermark persists
        var buffer2 = new InMemoryTransactionsBuffer();
        var journal = new FileRecoveryJournal(dir);
        var state = new TransactionState { TsId = 1, Completion = CompletionOperation.Commit };
        state.WriteStatements.Add(new SqlStatementInfo
        {
            Sql = "INSERT INTO Items (Id, Name) VALUES (1, 'a')",
            IsWrite = true,
            Sets = new ReadWriteSets()
        });
        await journal.AppendAsync(new JournalEntry
        {
            TsId = 1,
            TransactionId = Guid.NewGuid(),
            Completion = CompletionOperation.Commit,
            Status = JournalEntryStatus.Published,
            PayloadJson = FileRecoveryJournal.SerializeState(state)
        });

        await using (var tm2 = new HihooiTransactionManager(options, provider, buffer: buffer2))
        {
            await tm2.StartAsync();
            await Task.Delay(200);
            // Fake store already has row id=1; a second apply would throw/duplicate — count stays 1
            var rows = provider.GetStore("ext1").Query("SELECT * FROM Items WHERE Id = 1", null);
            Assert.Single(rows);
        }

        try { Directory.Delete(dir, true); } catch { /* ignore */ }
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
