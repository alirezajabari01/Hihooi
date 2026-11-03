using Hihooi.Core.Indexes;
using Hihooi.Core.Model;

namespace Hihooi.Tests.Algorithms;

public class ReplicaIndexesTests
{
    [Fact]
    public void UpdateIndexes_and_FindLatestConsistentTsId_match_paper_examples()
    {
        var idx = new ReplicaIndexes();

        idx.UpdateIndexes(Write(11, "R", AffectingClass.Ras, cols: new[] { "A2", "A3" }, rows: new[] { "A1=100" }, readCols: new[] { "A1" }), 11);
        idx.UpdateIndexes(Write(12, "S", AffectingClass.Cas, cols: new[] { "B2" }, readCols: new[] { "B5" }), 12);
        idx.UpdateIndexes(Write(13, "R", AffectingClass.Cas, cols: new[] { "A3", "A4" }, readCols: new[] { "A2" }), 13);
        idx.UpdateIndexes(Write(14, "R", AffectingClass.Ras, cols: new[] { "*" }, rows: new[] { "A1=120" }, readCols: new[] { "A1" }), 14);
        idx.UpdateIndexes(Write(15, "S", AffectingClass.Cas, cols: new[] { "B4" }, readCols: new[] { "B5" }), 15);

        // R3: SELECT B2,B3 FROM S WHERE B5 < ? → CAS → TSID 12
        var r3 = new ReadWriteSets();
        r3.TableReadSet.Add("S");
        r3.ColumnReadSets["S"] = new RefCountSet();
        r3.ColumnReadSets["S"].AddRange(new[] { "B2", "B3", "B5" });
        r3.ClassByTable["S"] = AffectingClass.Cas;
        Assert.Equal(12, idx.FindLatestConsistentTsId(r3));

        // R1: SELECT * FROM R → TAS → 14
        var r1 = new ReadWriteSets();
        r1.TableReadSet.Add("R");
        r1.ColumnReadSets["R"] = new RefCountSet();
        r1.ColumnReadSets["R"].Add("*");
        r1.ClassByTable["R"] = AffectingClass.Tas;
        Assert.Equal(14, idx.FindLatestConsistentTsId(r1));
    }

    private static ReadWriteSets Write(long _, string table, AffectingClass cls, string[]? cols = null, string[]? rows = null, string[]? readCols = null)
    {
        var s = new ReadWriteSets();
        s.TableWriteSet.Add(table);
        s.TableReadSet.Add(table);
        s.ClassByTable[table] = cls;
        if (cols is not null) { s.ColumnWriteSets[table] = new RefCountSet(); s.ColumnWriteSets[table].AddRange(cols); }
        if (readCols is not null) { s.ColumnReadSets[table] = new RefCountSet(); s.ColumnReadSets[table].AddRange(readCols); }
        if (rows is not null) { s.RowWriteSets[table] = new RefCountSet(); s.RowWriteSets[table].AddRange(rows); }
        return s;
    }
}
