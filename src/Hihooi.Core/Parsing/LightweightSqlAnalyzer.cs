using System.Text.RegularExpressions;
using Hihooi.Core.Model;

namespace Hihooi.Core.Parsing;

/// <summary>
/// Lightweight T-SQL analyzer for R/W sets and affecting classes (paper §4.1).
/// Supports common DML/DDL patterns; complex SQL falls back to TAS.
/// </summary>
public sealed partial class LightweightSqlAnalyzer : ISqlAnalyzer
{
    public SqlStatementInfo Analyze(string sql, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        var trimmed = sql.Trim().TrimEnd(';');
        var sets = new ReadWriteSets();
        var upper = trimmed.ToUpperInvariant();
        var isWrite = false;
        var usedSafeFallback = false;

        // Multi-batch / dynamic / proc / temp → conservative TAS on all referenced tables.
        if (RequiresSafeFallback(upper))
        {
            usedSafeFallback = true;
            isWrite = LooksLikeWrite(upper);
            ApplyConservativeTas(trimmed, sets, isWrite);
        }
        else if (upper.StartsWith("WITH", StringComparison.Ordinal))
        {
            // CTE: safe fallback (cannot reliably extract nested R/W sets)
            usedSafeFallback = true;
            isWrite = LooksLikeWrite(upper);
            ApplyConservativeTas(trimmed, sets, isWrite);
        }
        else if (upper.StartsWith("SELECT", StringComparison.Ordinal))
        {
            if (HasJoinOrSubquery(upper))
            {
                usedSafeFallback = true;
                ApplyConservativeTas(trimmed, sets, write: false);
            }
            else
                ParseSelect(trimmed, sets);
        }
        else if (upper.StartsWith("INSERT", StringComparison.Ordinal))
        {
            isWrite = true;
            if (upper.Contains(" SELECT ", StringComparison.Ordinal) || HasJoinOrSubquery(upper))
            {
                usedSafeFallback = true;
                ApplyConservativeTas(trimmed, sets, write: true);
            }
            else
                ParseInsert(trimmed, sets);
        }
        else if (upper.StartsWith("UPDATE", StringComparison.Ordinal))
        {
            isWrite = true;
            if (HasJoinOrSubquery(upper) || upper.Contains(" FROM ", StringComparison.Ordinal))
            {
                // UPDATE ... FROM / JOIN → safe TAS
                usedSafeFallback = true;
                ApplyConservativeTas(trimmed, sets, write: true);
            }
            else
                ParseUpdate(trimmed, sets);
        }
        else if (upper.StartsWith("DELETE", StringComparison.Ordinal))
        {
            isWrite = true;
            if (HasJoinOrSubquery(upper))
            {
                usedSafeFallback = true;
                ApplyConservativeTas(trimmed, sets, write: true);
            }
            else
                ParseDelete(trimmed, sets);
        }
        else if (IsDdl(upper))
        {
            isWrite = true;
            ParseDdl(trimmed, sets);
        }
        else
        {
            usedSafeFallback = true;
            isWrite = LooksLikeWrite(upper);
            ApplyConservativeTas(trimmed, sets, isWrite);
        }

        // Sequence / NEXT VALUE / IDENTITY-related reads → write (Table IV)
        if (!isWrite && (upper.Contains("NEXT VALUE FOR", StringComparison.Ordinal)
                         || upper.Contains("IDENT_CURRENT", StringComparison.Ordinal)
                         || upper.Contains("NEWID(", StringComparison.Ordinal)
                         || upper.Contains("NEWSEQUENTIALID(", StringComparison.Ordinal)))
        {
            isWrite = true;
            if (sets.TableWriteSet.IsEmpty && !sets.TableReadSet.IsEmpty)
            {
                foreach (var t in sets.TableReadSet.Counts.Keys.ToList())
                {
                    sets.TableWriteSet.Add(t);
                    EnsureCol(sets.ColumnWriteSets, t).Add("*");
                    sets.ClassByTable[t] = AffectingClass.Tas;
                }
            }
        }

        var rewritten = RewriteNonDeterministic(trimmed);
        _ = usedSafeFallback; // retained for future telemetry hooks

        return new SqlStatementInfo
        {
            Sql = trimmed,
            Parameters = parameters ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase),
            Sets = sets,
            IsWrite = isWrite,
            RewrittenSql = rewritten
        };
    }

    private static bool RequiresSafeFallback(string upper) =>
        upper.Contains(" EXEC ", StringComparison.Ordinal)
        || upper.StartsWith("EXEC ", StringComparison.Ordinal)
        || upper.StartsWith("EXECUTE ", StringComparison.Ordinal)
        || upper.Contains("SP_EXECUTESQL", StringComparison.Ordinal)
        || upper.Contains("#", StringComparison.Ordinal) // temp tables
        || upper.Contains(" OPENROWSET", StringComparison.Ordinal)
        || upper.Contains(" OPENQUERY", StringComparison.Ordinal)
        || upper.Contains('\n') && upper.Contains(';'); // multi-statement batch

    private static bool HasJoinOrSubquery(string upper) =>
        upper.Contains(" JOIN ", StringComparison.Ordinal)
        || upper.Contains("(SELECT", StringComparison.Ordinal)
        || upper.Contains(" APPLY ", StringComparison.Ordinal);

    private static bool LooksLikeWrite(string upper) =>
        upper.Contains("INSERT", StringComparison.Ordinal)
        || upper.Contains("UPDATE", StringComparison.Ordinal)
        || upper.Contains("DELETE", StringComparison.Ordinal)
        || upper.Contains("MERGE", StringComparison.Ordinal)
        || upper.Contains("TRUNCATE", StringComparison.Ordinal)
        || upper.Contains("CREATE", StringComparison.Ordinal)
        || upper.Contains("ALTER", StringComparison.Ordinal)
        || upper.Contains("DROP", StringComparison.Ordinal)
        || upper.Contains("EXEC", StringComparison.Ordinal);

    /// <summary>
    /// Safe path: mark all referenced tables as TAS. Never claim fine-grained independence.
    /// </summary>
    private static void ApplyConservativeTas(string sql, ReadWriteSets sets, bool write)
    {
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in TableNameRegex().Matches(sql))
            tables.Add(NormalizeIdent(m.Groups[1].Value));
        foreach (Match m in FromJoinRegex().Matches(sql))
            tables.Add(NormalizeIdent(m.Groups[1].Value));

        if (tables.Count == 0)
            tables.Add("__unknown__");

        foreach (var t in tables)
        {
            sets.TableReadSet.Add(t);
            if (write)
            {
                sets.TableWriteSet.Add(t);
                EnsureCol(sets.ColumnWriteSets, t).Add("*");
            }
            else
                EnsureCol(sets.ColumnReadSets, t).Add("*");
            sets.ClassByTable[t] = AffectingClass.Tas;
        }
    }

    private static void ParseSelect(string sql, ReadWriteSets sets)
    {
        // SELECT cols FROM table [JOIN ...] WHERE ...
        var fromMatches = FromJoinRegex().Matches(sql);
        var tables = new List<string>();
        foreach (Match m in fromMatches)
            tables.Add(NormalizeIdent(m.Groups[1].Value));

        if (tables.Count == 0)
        {
            // fallback
            foreach (Match m in TableNameRegex().Matches(sql))
                tables.Add(NormalizeIdent(m.Groups[1].Value));
        }

        foreach (var t in tables.Distinct(StringComparer.OrdinalIgnoreCase))
            sets.TableReadSet.Add(t);

        var selectList = ExtractSelectList(sql);
        var where = ExtractWhere(sql);

        if (selectList.Contains("*") || tables.Count > 1 && selectList.Count == 0)
        {
            foreach (var t in tables)
            {
                EnsureCol(sets.ColumnReadSets, t).Add("*");
            }
        }
        else
        {
            foreach (var t in tables)
            {
                foreach (var col in selectList)
                {
                    var c = col.Contains('.') ? col.Split('.').Last() : col;
                    EnsureCol(sets.ColumnReadSets, t).Add(NormalizeIdent(c));
                }
                foreach (var col in ExtractColumnsFromPredicate(where))
                    EnsureCol(sets.ColumnReadSets, t).Add(col);
            }
        }

        foreach (var t in tables)
        {
            foreach (var row in ExtractPkEqualities(where))
                EnsureCol(sets.RowReadSets, t).Add(row);
        }

        sets.RecomputeClasses();
        // SELECT * → TAS; PK equality without * → RAS; else CAS
        foreach (var t in tables)
        {
            if (sets.ColumnReadSets.TryGetValue(t, out var cols) && cols.Counts.ContainsKey("*"))
                sets.ClassByTable[t] = AffectingClass.Tas;
            else if (sets.RowReadSets.TryGetValue(t, out var rows) && rows.Counts.Count > 0)
                sets.ClassByTable[t] = AffectingClass.Ras;
            else
                sets.ClassByTable[t] = AffectingClass.Cas;
        }
    }

    private static void ParseInsert(string sql, ReadWriteSets sets)
    {
        var m = InsertRegex().Match(sql);
        if (!m.Success) return;
        var table = NormalizeIdent(m.Groups[1].Value);
        sets.TableWriteSet.Add(table);
        sets.TableReadSet.Add(table);
        // Paper Table II: insert column write set is R.* (full row image) → TAS.
        EnsureCol(sets.ColumnWriteSets, table).Add("*");
        sets.ClassByTable[table] = AffectingClass.Tas;
    }

    private static void ParseUpdate(string sql, ReadWriteSets sets)
    {
        var m = UpdateRegex().Match(sql);
        if (!m.Success) return;
        var table = NormalizeIdent(m.Groups[1].Value);
        sets.TableWriteSet.Add(table);
        sets.TableReadSet.Add(table);

        var setClause = m.Groups["set"].Value;
        var where = m.Groups["where"].Success ? m.Groups["where"].Value : "";

        foreach (Match cm in SetColumnRegex().Matches(setClause))
            EnsureCol(sets.ColumnWriteSets, table).Add(NormalizeIdent(cm.Groups[1].Value));

        foreach (var col in ExtractColumnsFromPredicate(where))
            EnsureCol(sets.ColumnReadSets, table).Add(col);

        var rows = ExtractPkEqualities(where);
        foreach (var r in rows)
            EnsureCol(sets.RowWriteSets, table).Add(r);

        if (rows.Count > 0)
            sets.ClassByTable[table] = AffectingClass.Ras;
        else if (sets.ColumnWriteSets.TryGetValue(table, out var cw) && cw.Counts.Count > 0)
            sets.ClassByTable[table] = AffectingClass.Cas;
        else
        {
            EnsureCol(sets.ColumnWriteSets, table).Add("*");
            sets.ClassByTable[table] = AffectingClass.Tas;
        }
    }

    private static void ParseDelete(string sql, ReadWriteSets sets)
    {
        var m = DeleteRegex().Match(sql);
        if (!m.Success) return;
        var table = NormalizeIdent(m.Groups[1].Value);
        sets.TableWriteSet.Add(table);
        sets.TableReadSet.Add(table);
        EnsureCol(sets.ColumnWriteSets, table).Add("*");

        var where = m.Groups["where"].Success ? m.Groups["where"].Value : "";
        foreach (var col in ExtractColumnsFromPredicate(where))
            EnsureCol(sets.ColumnReadSets, table).Add(col);

        var rows = ExtractPkEqualities(where);
        foreach (var r in rows)
            EnsureCol(sets.RowWriteSets, table).Add(r);

        sets.ClassByTable[table] = rows.Count > 0 ? AffectingClass.Ras : AffectingClass.Tas;
    }

    private static void ParseDdl(string sql, ReadWriteSets sets)
    {
        var m = DdlTableRegex().Match(sql);
        if (m.Success)
        {
            var table = NormalizeIdent(m.Groups[1].Value);
            sets.TableWriteSet.Add(table);
            EnsureCol(sets.ColumnWriteSets, table).Add("*");
            sets.ClassByTable[table] = AffectingClass.Tas;
        }
    }

    private static bool IsDdl(string upper) =>
        upper.StartsWith("CREATE", StringComparison.Ordinal)
        || upper.StartsWith("ALTER", StringComparison.Ordinal)
        || upper.StartsWith("DROP", StringComparison.Ordinal)
        || upper.StartsWith("TRUNCATE", StringComparison.Ordinal);

    private static string? RewriteNonDeterministic(string sql)
    {
        // Replace common non-deterministic macros with a fixed UTC literal for replica replay.
        // Adaptation of Table IV for T-SQL.
        var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fff");
        var rewritten = SysDateTimeRegex().Replace(sql, $"'{now}'");
        rewritten = GetDateRegex().Replace(rewritten, $"'{now}'");
        rewritten = RandRegex().Replace(rewritten, "0"); /* RAND substituted once — see SqlServer provider for stronger handling */
        return rewritten == sql ? null : rewritten;
    }

    private static List<string> ExtractSelectList(string sql)
    {
        var m = SelectListRegex().Match(sql);
        if (!m.Success) return new List<string>();
        return m.Groups[1].Value.Split(',')
            .Select(s => NormalizeIdent(s.Split(new[] { " AS ", " as " }, StringSplitOptions.None)[0].Trim()))
            .Where(s => s.Length > 0)
            .ToList();
    }

    private static string ExtractWhere(string sql)
    {
        var m = WhereRegex().Match(sql);
        return m.Success ? m.Groups[1].Value : "";
    }

    private static List<string> ExtractColumnsFromPredicate(string pred)
    {
        var list = new List<string>();
        foreach (Match m in IdentRegex().Matches(pred))
            list.Add(NormalizeIdent(m.Value));
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> ExtractPkEqualities(string pred)
    {
        // Col = literal or Col = @p — treat as row key candidate (paper: PK/unique equality)
        var list = new List<string>();
        foreach (Match m in PkEqualityRegex().Matches(pred))
        {
            var col = NormalizeIdent(m.Groups[1].Value);
            var val = m.Groups[2].Value.Trim();
            list.Add($"{col}={val}");
        }
        return list;
    }

    private static RefCountSet EnsureCol(Dictionary<string, RefCountSet> dict, string table)
    {
        if (!dict.TryGetValue(table, out var s))
        {
            s = new RefCountSet();
            dict[table] = s;
        }
        return s;
    }

    private static string NormalizeIdent(string id)
    {
        id = id.Trim().Trim('[', ']', '"', '`');
        if (id.Contains('.')) id = id.Split('.').Last();
        return id;
    }

    [GeneratedRegex(@"\b(?:FROM|JOIN)\s+(\[?[A-Za-z0-9_\.]+\]?)", RegexOptions.IgnoreCase)]
    private static partial Regex FromJoinRegex();

    [GeneratedRegex(@"\b(?:INTO|UPDATE|FROM|TABLE|JOIN)\s+(\[?[A-Za-z0-9_\.]+\]?)", RegexOptions.IgnoreCase)]
    private static partial Regex TableNameRegex();

    [GeneratedRegex(@"^INSERT\s+INTO\s+(\[?[A-Za-z0-9_\.]+\]?)\s*(?:\((?<cols>[^\)]*)\))?", RegexOptions.IgnoreCase)]
    private static partial Regex InsertRegex();

    [GeneratedRegex(@"^UPDATE\s+(\[?[A-Za-z0-9_\.]+\]?)\s+SET\s+(?<set>.+?)(?:\s+WHERE\s+(?<where>.+))?$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex UpdateRegex();

    [GeneratedRegex(@"^DELETE\s+FROM\s+(\[?[A-Za-z0-9_\.]+\]?)(?:\s+WHERE\s+(?<where>.+))?$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex DeleteRegex();

    [GeneratedRegex(@"\b(?:TABLE)\s+(\[?[A-Za-z0-9_\.]+\]?)", RegexOptions.IgnoreCase)]
    private static partial Regex DdlTableRegex();

    [GeneratedRegex(@"^SELECT\s+(.*?)\s+FROM\s+", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex SelectListRegex();

    [GeneratedRegex(@"\bWHERE\s+(.+?)(?:\s+ORDER\s+BY|\s+GROUP\s+BY|\s+OPTION\s*|\s*$)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex WhereRegex();

    [GeneratedRegex(@"\[?[A-Za-z_][A-Za-z0-9_]*\]?(?=\s*=)")]
    private static partial Regex IdentRegex();

    [GeneratedRegex(@"(\[?[A-Za-z_][A-Za-z0-9_]*\]?)\s*=\s*('[^']*'|\d+|@[A-Za-z0-9_]+)", RegexOptions.IgnoreCase)]
    private static partial Regex PkEqualityRegex();

    [GeneratedRegex(@"(\[?[A-Za-z_][A-Za-z0-9_]*\]?)\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex SetColumnRegex();

    [GeneratedRegex(@"\bSYS(?:UTC)?DATETIME(?:OFFSET)?\s*\(\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex SysDateTimeRegex();

    [GeneratedRegex(@"\bGET(?:UTC)?DATE\s*\(\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex GetDateRegex();

    [GeneratedRegex(@"\bRAND\s*\(\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex RandRegex();
}
