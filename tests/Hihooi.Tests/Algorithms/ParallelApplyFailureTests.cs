using Hihooi.Core.Algorithms;
using Hihooi.Core.Model;

namespace Hihooi.Tests.Algorithms;

public class ParallelApplyFailureTests
{
    [Fact]
    public void OnTransactionComplete_releases_waiters_after_failed_runner_slot_freed()
    {
        // Simulates Extractor fix: even if apply fails, scheduler must call OnTransactionComplete.
        var sched = new ParallelApplyScheduler();
        var w1 = Make("R", AffectingClass.Tas, "*");
        var w2 = Make("R", AffectingClass.Cas, "A2"); // conflicts with W1

        Assert.Single(sched.OnNewTransaction(w1));
        Assert.Empty(sched.OnNewTransaction(w2));

        var released = sched.OnTransactionComplete(w1);
        Assert.Contains(released, t => t.TransactionId == w2.TransactionId);
    }

    private static TransactionState Make(string table, AffectingClass cls, string col)
    {
        var s = new TransactionState();
        s.Sets.TableWriteSet.Add(table);
        s.Sets.TableReadSet.Add(table);
        s.Sets.ClassByTable[table] = cls;
        s.Sets.ColumnWriteSets[table] = new RefCountSet();
        s.Sets.ColumnWriteSets[table].Add(col);
        return s;
    }
}
