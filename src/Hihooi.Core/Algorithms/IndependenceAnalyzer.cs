using Hihooi.Core.Model;

namespace Hihooi.Core.Algorithms;

/// <summary>Algorithm 1 — areIndependent (paper §4.2).</summary>
public static class IndependenceAnalyzer
{
    public static bool AreIndependent(ReadWriteSets ts1, ReadWriteSets ts2)
    {
        // if table_w ∩ table_rw (cross) empty both ways → independent
        if (!ts1.TableWriteSet.Intersects(ts2.TableReadWriteSet)
            && !ts2.TableWriteSet.Intersects(ts1.TableReadWriteSet))
        {
            return true;
        }

        var common = ts1.TableReadWriteSet.ToHashSet();
        common.IntersectWith(ts2.TableReadWriteSet.ToHashSet());
        // Table-level conflict without a common rw table is unexpected; treat as dependent.
        if (common.Count == 0)
            return false;

        foreach (var table in common)
        {
            if (!IsIndependentForTable(ts1, ts2, table))
                return false;
        }

        return true;
    }

    public static bool AreIndependent(TransactionState a, TransactionState b)
        => AreIndependent(a.Sets, b.Sets);

    private static AffectingClass ClassOf(ReadWriteSets sets, string table)
        => sets.ClassByTable.TryGetValue(table, out var c) ? c : sets.InferClass(table);

    private static bool IsIndependentForTable(ReadWriteSets ts1, ReadWriteSets ts2, string table)
    {
        var c1 = ClassOf(ts1, table);
        var c2 = ClassOf(ts2, table);

        if (c1 == AffectingClass.Cas && c2 == AffectingClass.Cas)
        {
            var w1 = GetCol(ts1.ColumnWriteSets, table);
            var rw2 = ts2.GetColumnReadWriteSet(table);
            var w2 = GetCol(ts2.ColumnWriteSets, table);
            var rw1 = ts1.GetColumnReadWriteSet(table);
            if (!w1.Intersects(rw2) && !w2.Intersects(rw1))
                return true;
        }

        if (c1 == AffectingClass.Ras && c2 == AffectingClass.Ras)
        {
            var w1 = GetCol(ts1.RowWriteSets, table);
            var rw2 = ts2.GetRowReadWriteSet(table);
            var w2 = GetCol(ts2.RowWriteSets, table);
            var rw1 = ts1.GetRowReadWriteSet(table);
            if (!w1.Intersects(rw2) && !w2.Intersects(rw1))
                return true;
        }

        return false;
    }

    private static RefCountSet GetCol(Dictionary<string, RefCountSet> dict, string table)
        => dict.TryGetValue(table, out var s) ? s : new RefCountSet();
}
