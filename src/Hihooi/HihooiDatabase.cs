using Hihooi.Core.Diagnostics.Tracing;
using Hihooi.Core.Mapping;
using Hihooi.Core.TransactionManager;

namespace Hihooi;

/// <summary>
/// Default <see cref="IHihooiDatabase"/> — opens a short-lived engine session per call
/// so connections are never shared across concurrent requests.
/// </summary>
public sealed class HihooiDatabase : IHihooiDatabase
{
    private readonly HihooiTransactionManager _tm;
    private readonly IHihooiTracer _tracer;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private volatile bool _started;

    public HihooiDatabase(HihooiTransactionManager transactionManager, IHihooiTracer? tracer = null)
    {
        _tm = transactionManager ?? throw new ArgumentNullException(nameof(transactionManager));
        _tracer = tracer ?? transactionManager.Tracer;
    }

    public async Task<int> ExecuteAsync(
        string sql,
        object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        using var scope = _tracer.StartRequest(nameof(ExecuteAsync), RequestKind.Write, sql, parameters);
        var previous = HihooiTraceContext.Scope;
        HihooiTraceContext.Scope = scope;
        try
        {
            await using var session = _tm.OpenSession();
            var affected = await session.ExecuteNonQueryAsync(sql, parameters, cancellationToken).ConfigureAwait(false);
            if (scope is not null) scope.Trace.RowsAffected = affected;
            return affected;
        }
        catch (Exception ex)
        {
            scope?.Fail(ex.Message);
            throw;
        }
        finally
        {
            HihooiTraceContext.Scope = previous;
        }
    }

    public async Task<IReadOnlyList<T>> QueryAsync<T>(
        string sql,
        object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        using var scope = _tracer.StartRequest(nameof(QueryAsync), RequestKind.Read, sql, parameters);
        var previous = HihooiTraceContext.Scope;
        HihooiTraceContext.Scope = scope;
        try
        {
            await using var session = _tm.OpenSession();
            var mapSpan = scope?.StartSpan("result.materialize", "RowMapper");
            var rows = await session.QueryAsync(sql, parameters, cancellationToken).ConfigureAwait(false);
            var mapped = RowMapper.MapAll<T>(rows);
            if (mapSpan is not null)
            {
                scope!.SetAttr(mapSpan, "mapped_type", typeof(T).Name);
                scope.SetAttr(mapSpan, "rows", mapped.Count.ToString());
                scope.EndSpan(mapSpan);
            }
            if (scope is not null) scope.Trace.RowsReturned = mapped.Count;
            return mapped;
        }
        catch (Exception ex)
        {
            scope?.Fail(ex.Message);
            throw;
        }
        finally
        {
            HihooiTraceContext.Scope = previous;
        }
    }

    public async Task<T?> QueryFirstOrDefaultAsync<T>(
        string sql,
        object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        var rows = await QueryAsync<T>(sql, parameters, cancellationToken).ConfigureAwait(false);
        return rows.Count == 0 ? default : rows[0];
    }

    public async Task<T> QuerySingleAsync<T>(
        string sql,
        object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        var rows = await QueryAsync<T>(sql, parameters, cancellationToken).ConfigureAwait(false);
        return rows.Count switch
        {
            0 => throw new InvalidOperationException("Sequence contains no elements."),
            1 => rows[0],
            _ => throw new InvalidOperationException("Sequence contains more than one element.")
        };
    }

    internal async Task EnsureStartedAsync(CancellationToken cancellationToken = default)
    {
        if (_started) return;
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_started) return;
            await _tm.StartAsync(cancellationToken).ConfigureAwait(false);
            _started = true;
        }
        finally
        {
            _startGate.Release();
        }
    }
}
