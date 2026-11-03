using System.Data;
using Hihooi.Core.Abstractions;
using Microsoft.Data.SqlClient;

namespace Hihooi.SqlServer;

public sealed class SqlServerDatabaseSession : IDatabaseSession
{
    private readonly SqlConnection _connection;
    private readonly int _commandTimeoutSeconds;
    private SqlTransaction? _transaction;
    private int _disposed;

    public SqlServerDatabaseSession(SqlConnection connection, string replicaId, int commandTimeoutSeconds = 30)
    {
        _connection = connection;
        ReplicaId = replicaId;
        _commandTimeoutSeconds = commandTimeoutSeconds;
    }

    public string ReplicaId { get; }

    public async Task BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        if (_transaction is not null) return;
        try
        {
            _transaction = (SqlTransaction)await _connection
                .BeginTransactionAsync(IsolationLevel.Snapshot, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SqlException ex) when (ex.Number == 3952 || ex.Message.Contains("snapshot", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Snapshot Isolation is required but unavailable on this database. Enable ALLOW_SNAPSHOT_ISOLATION ON.",
                ex);
        }
    }

    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        if (_transaction is null) return;
        await _transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        await _transaction.DisposeAsync().ConfigureAwait(false);
        _transaction = null;
    }

    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        if (_transaction is null) return;
        try
        {
            await _transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await _transaction.DisposeAsync().ConfigureAwait(false);
            _transaction = null;
        }
    }

    public async Task<int> ExecuteNonQueryAsync(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var cmd = CreateCommand(sql, parameters);
            return await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqlException ex)
        {
            throw new InvalidOperationException($"SQL non-query failed on '{ReplicaId}': {ex.Message}", ex);
        }
    }

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteReaderAsync(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var cmd = CreateCommand(sql, parameters);
            var rows = new List<IReadOnlyDictionary<string, object?>>();
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(row);
            }
            return rows;
        }
        catch (SqlException ex)
        {
            throw new InvalidOperationException($"SQL query failed on '{ReplicaId}': {ex.Message}", ex);
        }
    }

    public async Task<DateTime> GetEngineUtcTimestampAsync(CancellationToken cancellationToken = default)
    {
        await using var cmd = CreateCommand("SELECT SYSUTCDATETIME();");
        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is DateTime dt ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : DateTime.UtcNow;
    }

    private SqlCommand CreateCommand(string sql, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        var cmd = _connection.CreateCommand();
        cmd.Transaction = _transaction;
        cmd.CommandText = sql;
        cmd.CommandTimeout = _commandTimeoutSeconds;
        if (parameters is not null)
        {
            foreach (var kv in parameters)
            {
                var p = cmd.CreateParameter();
                var name = kv.Key.StartsWith('@') ? kv.Key : "@" + kv.Key;
                p.ParameterName = name;
                p.Value = kv.Value ?? DBNull.Value;
                cmd.Parameters.Add(p);
            }
        }
        return cmd;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_transaction is not null)
        {
            try { await _transaction.RollbackAsync().ConfigureAwait(false); } catch { /* ignore */ }
            await _transaction.DisposeAsync().ConfigureAwait(false);
            _transaction = null;
        }
        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
