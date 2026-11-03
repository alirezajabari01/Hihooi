using System.Diagnostics;
using Hihooi.Core.Abstractions;
using Hihooi.Core.Consistency;
using Hihooi.Core.Diagnostics.Tracing;
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
    private readonly Stopwatch _sw = new();
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
        _primarySession ??= await OpenPrimaryTimedAsync(cancellationToken).ConfigureAwait(false);
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
        var scope = HihooiTraceContext.Scope;
        var analyzeSpan = scope?.StartSpan("classify.analyze", "SqlAnalyzer");
        SqlStatementInfo info;
        try
        {
            info = _analyzer.Analyze(sql, parameters);
            scope?.SetAttr(analyzeSpan!, "is_write", info.IsWrite.ToString());
            scope?.EndSpan(analyzeSpan!);
        }
        catch (Exception ex)
        {
            scope?.EndSpan(analyzeSpan!, SpanStatus.Error, ex.Message);
            throw;
        }

        if (!info.IsWrite)
            throw new InvalidOperationException("ExecuteNonQuery requires a write statement. Use QueryAsync for reads.");

        if (scope is not null)
        {
            scope.Trace.Kind = RequestKind.Write;
            scope.Trace.ConsistencyMode = _consistency.ToString();
            scope.Trace.SelectedNodeId = "primary";
        }

        _primarySession ??= await OpenPrimaryTimedAsync(cancellationToken).ConfigureAwait(false);
        if (!_inTransaction)
        {
            await BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var result = await ExecuteWriteCoreAsync(info, cancellationToken).ConfigureAwait(false);
            await CommitAsync(cancellationToken).ConfigureAwait(false);
            if (scope is not null) scope.Trace.RowsAffected = result;
            return result;
        }

        var rows = await ExecuteWriteCoreAsync(info, cancellationToken).ConfigureAwait(false);
        if (scope is not null) scope.Trace.RowsAffected = (scope.Trace.RowsAffected ?? 0) + rows;
        return rows;
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
        var scope = HihooiTraceContext.Scope;
        var analyzeSpan = scope?.StartSpan("classify.analyze", "SqlAnalyzer");
        SqlStatementInfo info;
        try
        {
            info = _analyzer.Analyze(sql, parameters);
            scope?.SetAttr(analyzeSpan!, "is_write", info.IsWrite.ToString());
            scope?.EndSpan(analyzeSpan!);
        }
        catch (Exception ex)
        {
            scope?.EndSpan(analyzeSpan!, SpanStatus.Error, ex.Message);
            throw;
        }

        if (info.IsWrite)
            throw new InvalidOperationException("QueryAsync requires a read statement. Use ExecuteNonQueryAsync for writes.");

        if (scope is not null)
        {
            scope.Trace.Kind = RequestKind.Read;
            scope.Trace.ConsistencyMode = _consistency.ToString();
        }

        // Only a pure read (no writes in this session) may leave the primary.
        // INSERT/UPDATE/DELETE, and any read mixed into a write, stay on primary.
        if (_hasWrites)
        {
            if (scope is not null)
            {
                scope.Trace.SelectedNodeId = "primary";
                scope.Trace.RouteReason = "write-session-stays-on-primary";
            }
            _primarySession ??= await OpenPrimaryTimedAsync(cancellationToken).ConfigureAwait(false);
            return await ExecuteOnSessionAsync(_primarySession, info, "primary", cancellationToken).ConfigureAwait(false);
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
        var scope = HihooiTraceContext.Scope;
        var span = scope?.StartSpan("sql.execute", "DatabaseSession", nodeId: "primary");
        scope?.SetAttr(span!, "sql", info.RewrittenSql ?? info.Sql);
        try
        {
            _hasWrites = true;
            _writeStatements.Add(info);
            _runningWriteSets.Merge(info.Sets);
            _building.MergeStatement(info);
            var sql = info.RewrittenSql ?? info.Sql;
            var affected = await _primarySession!.ExecuteNonQueryAsync(sql, info.Parameters, ct).ConfigureAwait(false);
            scope?.SetAttr(span!, "rows_affected", affected.ToString());
            scope?.EndSpan(span!);
            return affected;
        }
        catch (Exception ex)
        {
            scope?.EndSpan(span!, SpanStatus.Error, ex.Message);
            scope?.Fail(ex.Message);
            throw;
        }
    }

    private async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteReadRoutedAsync(
        SqlStatementInfo info, CancellationToken ct)
    {
        var scope = HihooiTraceContext.Scope;
        var routeSpan = scope?.StartSpan("routing.decision", "QueryRouter");
        RouteDecision decision;
        try
        {
            decision = _tm.Router.RouteRead(info.Sets, _consistency, _tm.Options.RsiPcWaitTimeout);
            if (scope is not null)
            {
                scope.Trace.RouteReason = decision.Reason;
                scope.Trace.RoutingCandidates = decision.Candidates.Select(c => new RouteCandidateSnapshot
                {
                    NodeId = c.NodeId,
                    Eligible = c.Eligible,
                    Selected = c.Selected,
                    RejectReason = c.RejectReason,
                    AppliedTsId = c.AppliedTsId,
                    InflightReads = c.InflightReads,
                    HealthStatus = c.HealthStatus,
                    HealthLatencyMs = c.HealthLatencyMs
                }).ToList();
                scope.SetAttr(routeSpan!, "reason", decision.Reason);
                scope.SetAttr(routeSpan!, "required_tsid", decision.RequiredTsId.ToString());
                scope.SetAttr(routeSpan!, "target", decision.Target.ToString());
                scope.EndSpan(routeSpan!);
            }
        }
        catch (Exception ex)
        {
            scope?.EndSpan(routeSpan!, SpanStatus.Error, ex.Message);
            throw;
        }

        if (decision.Target == RouteTargetKind.Primary || decision.ExtensionId is null || decision.ConnectionString is null)
        {
            if (decision.Reason.Contains("fallback", StringComparison.OrdinalIgnoreCase)
                || decision.Reason.Contains("no-consistent", StringComparison.OrdinalIgnoreCase))
                _tm.Metrics.RecordPrimaryFallback(decision.Reason);
            else
                _tm.Metrics.RecordPrimaryRead(decision.Reason);
            if (scope is not null) scope.Trace.SelectedNodeId = "primary";
            _primarySession ??= await OpenPrimaryTimedAsync(ct).ConfigureAwait(false);
            return await ExecuteOnSessionAsync(_primarySession, info, "primary", ct).ConfigureAwait(false);
        }

        _tm.Metrics.RecordExtensionRead(decision.ExtensionId, decision.Reason);
        if (scope is not null) scope.Trace.SelectedNodeId = decision.ExtensionId;
        using var lease = _tm.Extensions.LeaseRead(decision.ExtensionId);
        var openSpan = scope?.StartSpan("connection.acquire", "DatabaseProvider", nodeId: decision.ExtensionId);
        IDatabaseSession session;
        try
        {
            var sw = Stopwatch.StartNew();
            session = await _tm.Provider.OpenSessionAsync(decision.ConnectionString, decision.ExtensionId, ct).ConfigureAwait(false);
            scope?.SetAttr(openSpan!, "acquire_ms", sw.Elapsed.TotalMilliseconds.ToString("F2"));
            scope?.EndSpan(openSpan!);
        }
        catch (Exception ex)
        {
            scope?.EndSpan(openSpan!, SpanStatus.Error, ex.Message);
            scope?.Fail(ex.Message);
            throw;
        }

        await using (session)
            return await ExecuteOnSessionAsync(session, info, decision.ExtensionId, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteOnSessionAsync(
        IDatabaseSession session,
        SqlStatementInfo info,
        string nodeId,
        CancellationToken ct)
    {
        var scope = HihooiTraceContext.Scope;
        var execSpan = scope?.StartSpan("sql.execute", "DatabaseSession", nodeId: nodeId);
        scope?.SetAttr(execSpan!, "sql", info.RewrittenSql ?? info.Sql);
        scope?.SetAttr(execSpan!, "replica_id", nodeId);
        try
        {
            var rows = await session.ExecuteReaderAsync(info.RewrittenSql ?? info.Sql, info.Parameters, ct).ConfigureAwait(false);
            scope?.SetAttr(execSpan!, "rows_returned", rows.Count.ToString());
            scope?.EndSpan(execSpan!);

            if (scope is not null)
            {
                scope.Trace.RowsReturned = rows.Count;
                var max = _tm.Options.Observability.MaxResultSampleRows;
                if (max > 0)
                {
                    var materialize = scope.StartSpan("result.materialize", "RowMapper", nodeId: nodeId);
                    scope.Trace.ResultSample = rows.Take(max).Select(r => (object?)r.ToDictionary(kv => kv.Key, kv => kv.Value)).ToList<object?>();
                    scope.EndSpan(materialize);
                }
            }

            return rows;
        }
        catch (Exception ex)
        {
            scope?.EndSpan(execSpan!, SpanStatus.Error, ex.Message);
            scope?.Fail(ex.Message);
            throw;
        }
    }

    private async Task<IDatabaseSession> OpenPrimaryTimedAsync(CancellationToken ct)
    {
        var scope = HihooiTraceContext.Scope;
        var span = scope?.StartSpan("connection.acquire", "DatabaseProvider", nodeId: "primary");
        try
        {
            var sw = Stopwatch.StartNew();
            var session = await _tm.OpenPrimarySessionAsync(ct).ConfigureAwait(false);
            scope?.SetAttr(span!, "acquire_ms", sw.Elapsed.TotalMilliseconds.ToString("F2"));
            scope?.EndSpan(span!);
            return session;
        }
        catch (Exception ex)
        {
            scope?.EndSpan(span!, SpanStatus.Error, ex.Message);
            scope?.Fail(ex.Message);
            throw;
        }
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
