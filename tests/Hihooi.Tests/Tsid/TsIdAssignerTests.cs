using Hihooi.Core.Model;
using Hihooi.Core.Tsid;

namespace Hihooi.Tests.Tsid;

public class TsIdAssignerTests
{
    [Fact]
    public async Task Assigns_ids_in_commit_timestamp_order_despite_completion_order()
    {
        var assigner = new TsIdAssigner();
        var early = new TransactionState { CommitTimestampUtc = DateTime.UtcNow };
        var late = new TransactionState { CommitTimestampUtc = early.CommitTimestampUtc.AddMilliseconds(5) };

        assigner.BeginCommitAttempt();
        assigner.BeginCommitAttempt();

        // late completes first
        var tLate = assigner.CompleteAsync(late);
        Assert.False(tLate.IsCompleted);

        var tEarly = assigner.CompleteAsync(early);
        await Task.WhenAll(tEarly, tLate);

        Assert.Equal(1, early.TsId);
        Assert.Equal(2, late.TsId);
        Assert.Equal(1, await tEarly);
        Assert.Equal(2, await tLate);
    }
}
