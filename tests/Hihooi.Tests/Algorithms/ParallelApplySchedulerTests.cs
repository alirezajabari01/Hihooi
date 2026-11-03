using Hihooi.Core.Algorithms;
using Hihooi.Core.Model;

namespace Hihooi.Tests.Algorithms;

public class ParallelApplySchedulerTests
{
    [Fact]
    public void Independent_transactions_run_in_parallel_conflicting_wait()
    {
        var sched = new ParallelApplyScheduler();
        var w1 = Ts("R", AffectingClass.Ras, "A1=100");
        var w2 = Ts("S", AffectingClass.Cas, null, "B2");
        var w3 = Ts("R", AffectingClass.Cas, null, "A3"); // conflicts with W1 same table RAS vs CAS → conflict

        Assert.Single(sched.OnNewTransaction(w1));
        Assert.Single(sched.OnNewTransaction(w2));
        Assert.Empty(sched.OnNewTransaction(w3));

        var released = sched.OnTransactionComplete(w1);
        Assert.Contains(released, t => t.TransactionId == w3.TransactionId);
    }

    [Fact]
    public void Waiting_conflict_blocks_even_if_independent_of_running()
    {
        // Paper example: W4 independent of running W1/W2 but waits behind W3
        var sched = new ParallelApplyScheduler();
        var w1 = Ts("R", AffectingClass.Ras, "A1=100", "A2");
        var w2 = Ts("S", AffectingClass.Cas, null, "B2");
        var w3 = Ts("R", AffectingClass.Cas, null, "A3");
        var w4 = Ts("R", AffectingClass.Ras, "A1=120", "*");

        sched.OnNewTransaction(w1);
        sched.OnNewTransaction(w2);
        Assert.Empty(sched.OnNewTransaction(w3));
        Assert.Empty(sched.OnNewTransaction(w4));
    }

    private static TransactionState Ts(string table, AffectingClass cls, string? row, string? col = null)
    {
        var s = new TransactionState();
        s.Sets.TableWriteSet.Add(table);
        s.Sets.TableReadSet.Add(table);
        s.Sets.ClassByTable[table] = cls;
        if (col is not null)
        {
            s.Sets.ColumnWriteSets[table] = new RefCountSet();
            s.Sets.ColumnWriteSets[table].Add(col);
        }
        if (row is not null)
        {
            s.Sets.RowWriteSets[table] = new RefCountSet();
            s.Sets.RowWriteSets[table].Add(row);
        }
        return s;
    }
}
