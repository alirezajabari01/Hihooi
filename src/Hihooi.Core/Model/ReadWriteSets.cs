namespace Hihooi.Core.Model;

/// <summary>Table / column / row read and write sets for a statement or merged transaction.</summary>
public sealed class ReadWriteSets
{
    public RefCountSet TableReadSet { get; } = new();
    public RefCountSet TableWriteSet { get; } = new();
    public Dictionary<string, RefCountSet> ColumnReadSets { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, RefCountSet> ColumnWriteSets { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, RefCountSet> RowReadSets { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, RefCountSet> RowWriteSets { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, AffectingClass> ClassByTable { get; } = new(StringComparer.OrdinalIgnoreCase);

    public RefCountSet TableReadWriteSet
    {
        get
        {
            var rw = TableReadSet.Clone();
            rw.Merge(TableWriteSet);
            return rw;
        }
    }

    public RefCountSet GetColumnReadWriteSet(string table)
    {
        var rw = GetOrEmpty(ColumnReadSets, table).Clone();
        rw.Merge(GetOrEmpty(ColumnWriteSets, table));
        return rw;
    }

    public RefCountSet GetRowReadWriteSet(string table)
    {
        var rw = GetOrEmpty(RowReadSets, table).Clone();
        rw.Merge(GetOrEmpty(RowWriteSets, table));
        return rw;
    }

    public void Merge(ReadWriteSets other)
    {
        TableReadSet.Merge(other.TableReadSet);
        TableWriteSet.Merge(other.TableWriteSet);
        MergeDict(ColumnReadSets, other.ColumnReadSets);
        MergeDict(ColumnWriteSets, other.ColumnWriteSets);
        MergeDict(RowReadSets, other.RowReadSets);
        MergeDict(RowWriteSets, other.RowWriteSets);
        foreach (var (table, cls) in other.ClassByTable)
        {
            if (!ClassByTable.TryGetValue(table, out var existing))
                ClassByTable[table] = cls;
            else
                ClassByTable[table] = MergeClass(existing, cls);
        }
    }

    public void Remove(ReadWriteSets other)
    {
        TableReadSet.Remove(other.TableReadSet);
        TableWriteSet.Remove(other.TableWriteSet);
        RemoveDict(ColumnReadSets, other.ColumnReadSets);
        RemoveDict(ColumnWriteSets, other.ColumnWriteSets);
        RemoveDict(RowReadSets, other.RowReadSets);
        RemoveDict(RowWriteSets, other.RowWriteSets);
        RecomputeClasses();
    }

    public ReadWriteSets Clone()
    {
        var c = new ReadWriteSets();
        c.Merge(this);
        return c;
    }

    public void Clear()
    {
        TableReadSet.Clear();
        TableWriteSet.Clear();
        ColumnReadSets.Clear();
        ColumnWriteSets.Clear();
        RowReadSets.Clear();
        RowWriteSets.Clear();
        ClassByTable.Clear();
    }

    public static AffectingClass MergeClass(AffectingClass a, AffectingClass b)
    {
        if (a == AffectingClass.Tas || b == AffectingClass.Tas) return AffectingClass.Tas;
        if (a == b) return a;
        // Mixed RAS/CAS → TAS (paper multi-statement rule iii)
        return AffectingClass.Tas;
    }

    public void RecomputeClasses()
    {
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in TableReadSet.Counts.Keys) tables.Add(t);
        foreach (var t in TableWriteSet.Counts.Keys) tables.Add(t);
        ClassByTable.Clear();
        foreach (var table in tables)
            ClassByTable[table] = InferClass(table);
    }

    public AffectingClass InferClass(string table)
    {
        var hasRows = GetOrEmpty(RowWriteSets, table).Counts.Count > 0
                      || GetOrEmpty(RowReadSets, table).Counts.Count > 0;
        var colW = GetOrEmpty(ColumnWriteSets, table);
        var colR = GetOrEmpty(ColumnReadSets, table);
        var star = colW.Counts.ContainsKey("*") || colR.Counts.ContainsKey("*");

        if (star) return AffectingClass.Tas;
        if (hasRows && !star) return AffectingClass.Ras;
        if (colW.Counts.Count > 0 || colR.Counts.Count > 0) return AffectingClass.Cas;
        return AffectingClass.Tas;
    }

    private static RefCountSet GetOrEmpty(Dictionary<string, RefCountSet> dict, string table)
        => dict.TryGetValue(table, out var s) ? s : new RefCountSet();

    private static void MergeDict(Dictionary<string, RefCountSet> target, Dictionary<string, RefCountSet> source)
    {
        foreach (var (k, v) in source)
        {
            if (!target.TryGetValue(k, out var existing))
            {
                existing = new RefCountSet();
                target[k] = existing;
            }
            existing.Merge(v);
        }
    }

    private static void RemoveDict(Dictionary<string, RefCountSet> target, Dictionary<string, RefCountSet> source)
    {
        foreach (var (k, v) in source)
        {
            if (!target.TryGetValue(k, out var existing)) continue;
            existing.Remove(v);
            if (existing.IsEmpty) target.Remove(k);
        }
    }
}
