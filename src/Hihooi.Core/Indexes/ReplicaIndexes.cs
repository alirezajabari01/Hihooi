using Hihooi.Core.Model;

namespace Hihooi.Core.Indexes;

/// <summary>
/// TIndex / CIndex / RIndex with Algorithm 3 and 4 (paper §5.1).
/// </summary>
public sealed class ReplicaIndexes
{
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _tIndex = new(StringComparer.OrdinalIgnoreCase);
    // key: table.column
    private readonly Dictionary<string, long> _cIndex = new(StringComparer.OrdinalIgnoreCase);
    // key: table|rowKey
    private readonly Dictionary<string, long> _rIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, long>> _rIndexByTable = new(StringComparer.OrdinalIgnoreCase);

    public void UpdateIndexes(TransactionState ts) => UpdateIndexes(ts.Sets, ts.TsId);

    /// <summary>Algorithm 3.</summary>
    public void UpdateIndexes(ReadWriteSets sets, long tsId)
    {
        lock (_gate)
        {
            foreach (var table in sets.TableWriteSet.Counts.Keys)
                _tIndex[table] = tsId;

            foreach (var table in sets.TableWriteSet.Counts.Keys)
            {
                var cls = sets.ClassByTable.TryGetValue(table, out var c) ? c : sets.InferClass(table);

                if (cls is AffectingClass.Tas or AffectingClass.Cas)
                {
                    if (sets.ColumnWriteSets.TryGetValue(table, out var cols))
                    {
                        foreach (var col in cols.Counts.Keys)
                            _cIndex[$"{table}.{col}"] = tsId;
                    }
                }
                else if (cls == AffectingClass.Ras)
                {
                    if (sets.RowWriteSets.TryGetValue(table, out var rows))
                    {
                        if (!_rIndexByTable.TryGetValue(table, out var perTable))
                        {
                            perTable = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                            _rIndexByTable[table] = perTable;
                        }

                        foreach (var row in rows.Counts.Keys)
                        {
                            var key = $"{table}|{row}";
                            _rIndex[key] = tsId;
                            perTable[row] = tsId;
                        }
                    }
                }
            }
        }
    }

    /// <summary>Algorithm 4.</summary>
    public long FindLatestConsistentTsId(ReadWriteSets readSets)
    {
        lock (_gate)
        {
            long tsid = 0;
            foreach (var table in readSets.TableReadSet.Counts.Keys)
            {
                if (!_tIndex.ContainsKey(table)) continue;

                var cls = readSets.ClassByTable.TryGetValue(table, out var c) ? c : readSets.InferClass(table);

                if (cls == AffectingClass.Tas)
                {
                    tsid = Math.Max(tsid, _tIndex[table]);
                }
                else if (cls == AffectingClass.Cas)
                {
                    // CIndex may store "*" for TAS/INSERT (Table II); treat as matching any column.
                    tsid = Math.Max(tsid, LookupColumns(table, readSets.ColumnReadSets));
                    tsid = Math.Max(tsid, MaxRowValue(table));
                }
                else if (cls == AffectingClass.Ras)
                {
                    if (readSets.RowReadSets.TryGetValue(table, out var rows))
                    {
                        foreach (var row in rows.Counts.Keys)
                        {
                            if (_rIndex.TryGetValue($"{table}|{row}", out var v))
                                tsid = Math.Max(tsid, v);
                        }
                    }
                    tsid = Math.Max(tsid, LookupColumns(table, readSets.ColumnReadSets));
                }
            }

            return tsid;
        }
    }

    public void PruneRowsAppliedEverywhere(long minAppliedOnAllReplicas)
    {
        lock (_gate)
        {
            var remove = _rIndex.Where(kv => kv.Value <= minAppliedOnAllReplicas).Select(kv => kv.Key).ToList();
            foreach (var key in remove)
            {
                _rIndex.Remove(key);
                var parts = key.Split('|', 2);
                if (parts.Length == 2 && _rIndexByTable.TryGetValue(parts[0], out var per))
                    per.Remove(parts[1]);
            }
        }
    }

    private long MaxRowValue(string table)
    {
        if (!_rIndexByTable.TryGetValue(table, out var per) || per.Count == 0) return 0;
        return per.Values.Max();
    }

    private long LookupColumns(string table, Dictionary<string, RefCountSet> columnReadSets)
    {
        long max = 0;
        if (_cIndex.TryGetValue($"{table}.*", out var star))
            max = Math.Max(max, star);

        if (!columnReadSets.TryGetValue(table, out var cols))
            return max;

        foreach (var col in cols.Counts.Keys)
        {
            if (col == "*")
            {
                // SELECT * / TAS read should consult TIndex (caller handles TAS),
                // but if "*" appears under CAS path, use TIndex via table key in CIndex star already.
                continue;
            }
            if (_cIndex.TryGetValue($"{table}.{col}", out var v))
                max = Math.Max(max, v);
        }
        return max;
    }
}
