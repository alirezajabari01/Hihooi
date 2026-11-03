using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Hihooi.Core.Abstractions;
using Hihooi.Core.Parsing;

namespace Hihooi.Tests.Fakes;

public sealed class FakeDatabaseProvider : IDatabaseProvider
{
    public string ProviderName => "Fake";
    private readonly ConcurrentDictionary<string, FakeStore> _stores = new(StringComparer.OrdinalIgnoreCase);

    public FakeStore GetStore(string replicaId) => _stores.GetOrAdd(replicaId, _ => new FakeStore());

    public Task<IDatabaseSession> OpenSessionAsync(string connectionString, string replicaId, CancellationToken cancellationToken = default)
    {
        var store = GetStore(replicaId);
        IDatabaseSession session = new FakeSession(store, replicaId);
        return Task.FromResult(session);
    }

    public Task EnsureSnapshotIsolationAsync(string connectionString, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

public sealed class FakeStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<string, Dictionary<string, object?>>> _tables = new(StringComparer.OrdinalIgnoreCase);

    public void EnsureTable(string table)
    {
        lock (_gate)
        {
            if (!_tables.ContainsKey(table))
                _tables[table] = new Dictionary<string, Dictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public int Execute(string sql, IReadOnlyDictionary<string, object?>? parameters)
    {
        lock (_gate)
        {
            sql = sql.Trim().TrimEnd(';');
            var upper = sql.ToUpperInvariant();
            if (upper.StartsWith("INSERT"))
            {
                var m = Regex.Match(sql, @"INSERT\s+INTO\s+(\w+)\s*\(([^)]+)\)\s*VALUES\s*\(([^)]+)\)", RegexOptions.IgnoreCase);
                if (!m.Success) return 0;
                var table = m.Groups[1].Value;
                EnsureTable(table);
                var cols = m.Groups[2].Value.Split(',').Select(c => c.Trim()).ToArray();
                var vals = SplitArgs(m.Groups[3].Value);
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < cols.Length; i++)
                    row[cols[i]] = Resolve(vals[i], parameters);
                var pk = row.TryGetValue("Id", out var id) ? Convert.ToString(id) ?? Guid.NewGuid().ToString() : Guid.NewGuid().ToString();
                _tables[table][pk!] = row;
                return 1;
            }
            if (upper.StartsWith("UPDATE"))
            {
                var m = Regex.Match(sql, @"UPDATE\s+(\w+)\s+SET\s+(.+?)\s+WHERE\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (!m.Success) return 0;
                var table = m.Groups[1].Value;
                EnsureTable(table);
                var setPart = m.Groups[2].Value;
                var where = m.Groups[3].Value;
                var assignments = Regex.Matches(setPart, @"(\w+)\s*=\s*([^,]+)");
                var count = 0;
                foreach (var (pk, row) in _tables[table].ToList())
                {
                    if (!MatchWhere(row, where, parameters)) continue;
                    foreach (Match a in assignments)
                    {
                        var col = a.Groups[1].Value;
                        var rhs = a.Groups[2].Value.Trim();
                        if (rhs.Contains('+', StringComparison.Ordinal))
                        {
                            var parts = rhs.Split('+').Select(x => x.Trim()).ToArray();
                            if (parts.Length == 2 && string.Equals(parts[0], col, StringComparison.OrdinalIgnoreCase))
                            {
                                var add = Convert.ToInt32(Resolve(parts[1], parameters) ?? 0);
                                var cur = Convert.ToInt32(row.TryGetValue(col, out var c) ? c ?? 0 : 0);
                                row[col] = cur + add;
                                continue;
                            }
                        }
                        row[col] = Resolve(rhs, parameters);
                    }
                    count++;
                }
                return count;
            }
            if (upper.StartsWith("DELETE"))
            {
                var m = Regex.Match(sql, @"DELETE\s+FROM\s+(\w+)(?:\s+WHERE\s+(.+))?$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (!m.Success) return 0;
                var table = m.Groups[1].Value;
                EnsureTable(table);
                var where = m.Groups[2].Success ? m.Groups[2].Value : null;
                var remove = _tables[table].Where(kv => where is null || MatchWhere(kv.Value, where, parameters)).Select(kv => kv.Key).ToList();
                foreach (var k in remove) _tables[table].Remove(k);
                return remove.Count;
            }
            return 0;
        }
    }

    public IReadOnlyList<IReadOnlyDictionary<string, object?>> Query(string sql, IReadOnlyDictionary<string, object?>? parameters)
    {
        lock (_gate)
        {
            sql = sql.Trim().TrimEnd(';');
            var m = Regex.Match(sql, @"SELECT\s+(.+?)\s+FROM\s+(\w+)(?:\s+WHERE\s+(.+))?$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!m.Success) return Array.Empty<IReadOnlyDictionary<string, object?>>();
            var table = m.Groups[2].Value;
            EnsureTable(table);
            var where = m.Groups[3].Success ? m.Groups[3].Value : null;
            var rows = _tables[table].Values
                .Where(r => where is null || MatchWhere(r, where, parameters))
                .Select(r => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(r, StringComparer.OrdinalIgnoreCase))
                .ToList();
            return rows;
        }
    }

    private static string[] SplitArgs(string s) => s.Split(',').Select(x => x.Trim()).ToArray();

    private static object? Resolve(string token, IReadOnlyDictionary<string, object?>? parameters)
    {
        if (token.StartsWith('@') && parameters is not null)
        {
            var key = SqlParameterBinder.NormalizeName(token);
            if (parameters.TryGetValue(key, out var named))
                return named;
            if (parameters.TryGetValue(token, out named))
                return named;
        }
        if (token.StartsWith('\'') && token.EndsWith('\'')) return token.Trim('\'');
        if (int.TryParse(token, out var n)) return n;
        return token;
    }

    private static bool MatchWhere(Dictionary<string, object?> row, string where, IReadOnlyDictionary<string, object?>? parameters)
    {
        var m = Regex.Match(where, @"(\w+)\s*=\s*([^\s]+)", RegexOptions.IgnoreCase);
        if (!m.Success) return true;
        var col = m.Groups[1].Value;
        var val = Resolve(m.Groups[2].Value, parameters);
        if (!row.TryGetValue(col, out var actual)) return false;
        return Equals(Convert.ToString(actual), Convert.ToString(val));
    }
}

public sealed class FakeSession : IDatabaseSession
{
    private readonly FakeStore _store;
    private bool _inTx;
    private readonly List<(string sql, IReadOnlyDictionary<string, object?>? pars, bool write)> _ops = new();

    public FakeSession(FakeStore store, string replicaId)
    {
        _store = store;
        ReplicaId = replicaId;
    }

    public string ReplicaId { get; }

    public Task BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        _inTx = true;
        _ops.Clear();
        return Task.CompletedTask;
    }

    public Task CommitAsync(CancellationToken cancellationToken = default)
    {
        foreach (var op in _ops)
        {
            if (op.write) _store.Execute(op.sql, op.pars);
        }
        _ops.Clear();
        _inTx = false;
        return Task.CompletedTask;
    }

    public Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        _ops.Clear();
        _inTx = false;
        return Task.CompletedTask;
    }

    public Task<int> ExecuteNonQueryAsync(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        if (_inTx)
        {
            _ops.Add((sql, parameters, true));
            return Task.FromResult(1);
        }
        return Task.FromResult(_store.Execute(sql, parameters));
    }

    public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteReaderAsync(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        if (_inTx && _ops.Count > 0)
        {
            foreach (var op in _ops.Where(o => o.write))
                _store.Execute(op.sql, op.pars);
            _ops.Clear();
        }
        return Task.FromResult(_store.Query(sql, parameters));
    }

    public Task<DateTime> GetEngineUtcTimestampAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(DateTime.UtcNow);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public void Dispose() { }
}
