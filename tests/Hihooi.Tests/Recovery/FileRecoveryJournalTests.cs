using Hihooi.Core.Model;
using Hihooi.Core.Recovery;

namespace Hihooi.Tests.Recovery;

public class FileRecoveryJournalTests
{
    [Fact]
    public async Task Journal_roundtrip_pending_entries()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hihooi-journal-test-" + Guid.NewGuid().ToString("N"));
        var journal = new FileRecoveryJournal(dir);
        var state = new TransactionState { TsId = 3, Completion = CompletionOperation.Commit };
        state.WriteStatements.Add(new SqlStatementInfo { Sql = "INSERT INTO R(Id) VALUES(1)", IsWrite = true, Sets = new ReadWriteSets() });

        await journal.AppendAsync(new JournalEntry
        {
            TsId = 3,
            TransactionId = state.TransactionId,
            Completion = CompletionOperation.Commit,
            Status = JournalEntryStatus.PendingPublish,
            PayloadJson = FileRecoveryJournal.SerializeState(state)
        });

        var pending = await journal.GetPendingAsync();
        Assert.Single(pending);
        await journal.UpdateStatusAsync(3, JournalEntryStatus.AppliedEverywhere);
        pending = await journal.GetPendingAsync();
        Assert.Empty(pending);

        try { Directory.Delete(dir, true); } catch { /* ignore */ }
    }
}
