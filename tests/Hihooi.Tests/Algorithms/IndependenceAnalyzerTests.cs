using Hihooi.Core.Algorithms;
using Hihooi.Core.Model;

namespace Hihooi.Tests.Algorithms;

public class IndependenceAnalyzerTests
{
    [Fact]
    public void Different_tables_are_independent()
    {
        var a = Write("R", AffectingClass.Ras, cols: new[] { "A2" }, rows: new[] { "A1=100" });
        var b = Write("S", AffectingClass.Cas, cols: new[] { "B2" });
        Assert.True(IndependenceAnalyzer.AreIndependent(a, b));
    }

    [Fact]
    public void Same_table_cas_disjoint_columns_independent()
    {
        var a = Write("S", AffectingClass.Cas, cols: new[] { "B2" }, readCols: new[] { "B5" });
        var b = Write("S", AffectingClass.Cas, cols: new[] { "B4" }, readCols: new[] { "B5" });
        Assert.True(IndependenceAnalyzer.AreIndependent(a, b));
    }

    [Fact]
    public void Same_table_overlapping_columns_conflict()
    {
        var a = Write("S", AffectingClass.Cas, cols: new[] { "B2" }, readCols: new[] { "B5" });
        var b = Write("S", AffectingClass.Cas, cols: new[] { "B2" }, readCols: new[] { "B5" });
        Assert.False(IndependenceAnalyzer.AreIndependent(a, b));
    }

    [Fact]
    public void Same_table_ras_disjoint_rows_independent()
    {
        var a = Write("R", AffectingClass.Ras, cols: new[] { "A2" }, rows: new[] { "A1=100" }, readCols: new[] { "A1" });
        var b = Write("R", AffectingClass.Ras, cols: new[] { "*" }, rows: new[] { "A1=120" }, readCols: new[] { "A1" });
        Assert.True(IndependenceAnalyzer.AreIndependent(a, b));
    }

    [Fact]
    public void Same_table_tas_conflicts()
    {
        var a = Write("R", AffectingClass.Tas, cols: new[] { "*" });
        var b = Write("R", AffectingClass.Cas, cols: new[] { "A2" });
        Assert.False(IndependenceAnalyzer.AreIndependent(a, b));
    }

    private static ReadWriteSets Write(string table, AffectingClass cls, string[]? cols = null, string[]? rows = null, string[]? readCols = null)
    {
        var s = new ReadWriteSets();
        s.TableWriteSet.Add(table);
        s.TableReadSet.Add(table);
        s.ClassByTable[table] = cls;
        if (cols is not null)
        {
            s.ColumnWriteSets[table] = new RefCountSet();
            s.ColumnWriteSets[table].AddRange(cols);
        }
        if (readCols is not null)
        {
            s.ColumnReadSets[table] = new RefCountSet();
            s.ColumnReadSets[table].AddRange(readCols);
        }
        if (rows is not null)
        {
            s.RowWriteSets[table] = new RefCountSet();
            s.RowWriteSets[table].AddRange(rows);
        }
        return s;
    }
}
