using Hihooi.Core.Abstractions;
using Hihooi.Core.Algorithms;
using Hihooi.Core.Consistency;
using Hihooi.Core.Model;
using Hihooi.Core.Parsing;
using Hihooi.Core.Routing;

namespace Hihooi.Core.TransactionManager;

/// <summary>
/// Engine session: intercepts SQL, routes reads/writes, builds TStates on completion.
/// Not part of the recommended public consumer API — use <c>IHihooiDatabase</c>.
/// </summary>
public sealed class HihooiSession : IAsyncDisposable
{
    private readonly HihooiTransactionManager _tm;
    private readonly ISqlAnalyzer _analyzer;
    private readonly ConsistencyLevel _consistency;
    private readonly List<SqlStatementInfo> _writeStatements = new();
    private readonly ReadWriteSets _runningWriteSets = new();
    private readonly TransactionState _building = new();
    private IDatabaseSession? _primarySession;
    private bool _inTransaction;
    private bool _hasWrites;
    private readonly System.Diagnostics.Stopwatch _sw = new();
    private int _disposed;

    internal HihooiSession(HihooiTransactionManager tm, ISqlAnalyzer analyzer, ConsistencyLevel consistency)
    {
        _tm = tm;
        _analyzer = analyzer;
        _consistency = consistency;
    }

    public ConsistencyLevel ConsistencyLevel => _consistency;

    public async Task BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        _primarySession ??= await _tm.OpenPrimarySessionAsync(cancellationToken).ConfigureAwait(false);
        await _primarySession.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        _inTransaction = true;
        _hasWrites = false;
        _writeStatements.Clear();
        _runningWriteSets.Clear();
        _building.WriteStatements.Clear();
        _building.Sets.Clear();
        _sw.Restart();
    }

    public Task<int> ExecuteNonQueryAsync(
        string sql,
        object? parameters = null,
        CancellationToken cancellationToken = default)
        => ExecuteNonQueryAsync(sql, SqlParameterBinder.Bind(parameters), cancellationToken);

    public async Task<int> ExecuteNonQueryAsync(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        var info = _analyzer.Analyze(sql, parameters);
        if (!info.IsWrite)
            throw new InvalidOperationException("ExecuteNonQuery requires a write statement. Use QueryAsync for reads.");

        _primarySession ??= await _tm.OpenPrimarySessionAsync(cancellationToken).ConfigureAwait(false);
        if (!_inTransaction)
        {
            await BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var result = await ExecuteWriteCoreAsync(info, cancellationToken).ConfigureAwait(false);
            await CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }

        return await ExecuteWriteCoreAsync(info, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(
        string sql,
        object? parameters = null,
        CancellationToken cancellationToken = default)
        => QueryAsync(sql, SqlParameterBinder.Bind(parameters), cancellationToken);

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        var info = _analyzer.Analyze(sql, parameters);

        if (info.IsWrite)
            throw new InvalidOperationException("QueryAsync requires a read statement. Use ExecuteNonQueryAsync for writes.");

        if (_inTransaction && _hasWrites)
        {
            if (IndependenceAnalyzer.AreIndependent(_runningWriteSets, info.Sets))
            {
                return await ExecuteReadRoutedAsync(info, cancellationToken).ConfigureAwait(false);
            }

            _primarySession ??= await _tm.OpenPrimarySessionAsync(cancellationToken).ConfigureAwait(false);
            return await _primarySession.ExecuteReaderAsync(info.RewrittenSql ?? info.Sql, info.Parameters, cancellationToken).ConfigureAwait(false);
        }

        return await ExecuteReadRoutedAsync(info, cancellationToken).ConfigureAwait(false);
    }

    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        if (!_inTransaction || _primarySession is null)
            return;

        if (!_hasWrites)
        {
            await _primarySession.CommitAsync(cancellationToken).ConfigureAwait(false);
            _inTransaction = false;
            return;
        }

        await _tm.CompleteWriteTransactionAsync(
            _building,
            _primarySession,
            _writeStatements,
            _runningWriteSets,
            _sw.Elapsed,
            CompletionOperation.Commit,
            isFailed: false,
            _consistency,
            cancellationToken).ConfigureAwait(false);

        _inTransaction = false;
        _hasWrites = false;
    }

    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        if (!_inTransaction || _primarySession is null)
            return;

        if (_hasWrites)
        {
            await _tm.CompleteWriteTransactionAsync(
                _building,
                _primarySession,
                _writeStatements,
                _runningWriteSets,
                _sw.Elapsed,
                CompletionOperation.Rollback,
                isFailed: false,
                _consistency,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _primarySession.RollbackAsync(cancellationToken).ConfigureAwait(false);
        }

        _inTransaction = false;
        _hasWrites = false;
    }

    private async Task<int> ExecuteWriteCoreAsync(SqlStatementInfo info, CancellationToken ct)
    {
        _hasWrites = true;
        _writeStatements.Add(info);
        _runningWriteSets.Merge(info.Sets);
        _building.MergeStatement(info);
        var sql = info.RewrittenSql ?? info.Sql;
        return await _primarySession!.ExecuteNonQueryAsync(sql, info.Parameters, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteReadRoutedAsync(
        SqlStatementInfo info, CancellationToken ct)
    {
        var decision = _tm.Router.RouteRead(info.Sets, _consistency, _tm.Options.RsiPcWaitTimeout);
        if (decision.Target == RouteTargetKind.Primary || decision.ExtensionId is null || decision.ConnectionString is null)
        {
            if (decision.Reason.Contains("fallback", StringComparison.OrdinalIgnoreCase)
                || decision.Reason.Contains("no-consistent", StringComparison.OrdinalIgnoreCase))
                _tm.Metrics.RecordPrimaryFallback(decision.Reason);
            else
                _tm.Metrics.RecordPrimaryRead(decision.Reason);
            _primarySession ??= await _tm.OpenPrimarySessionAsync(ct).ConfigureAwait(false);
            return await _primarySession.ExecuteReaderAsync(info.RewrittenSql ?? info.Sql, info.Parameters, ct).ConfigureAwait(false);
        }

        _tm.Metrics.RecordExtensionRead(decision.ExtensionId, decision.Reason);
        using var lease = _tm.Extensions.LeaseRead(decision.ExtensionId);
        await using var session = await _tm.Provider.OpenSessionAsync(decision.ConnectionString, decision.ExtensionId, ct).ConfigureAwait(false);
        return await session.ExecuteReaderAsync(info.RewrittenSql ?? info.Sql, info.Parameters, ct).ConfigureAwait(false);
    }

    private void EnsureNotDisposed()
    {
        if (_disposed != 0) throw new ObjectDisposedException(nameof(HihooiSession));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_inTransaction && _primarySession is not null)
        {
            try { await RollbackAsync().ConfigureAwait(false); } catch { /* ignore */ }
        }
        if (_primarySession is not null)
            await _primarySession.DisposeAsync().ConfigureAwait(false);
    }
}
