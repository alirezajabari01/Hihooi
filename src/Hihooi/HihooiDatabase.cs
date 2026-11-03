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
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private volatile bool _started;

    public HihooiDatabase(HihooiTransactionManager transactionManager)
    {
        _tm = transactionManager ?? throw new ArgumentNullException(nameof(transactionManager));
    }

    public async Task<int> ExecuteAsync(
        string sql,
        object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        await using var session = _tm.OpenSession();
        return await session.ExecuteNonQueryAsync(sql, parameters, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<T>> QueryAsync<T>(
        string sql,
        object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        await using var session = _tm.OpenSession();
        var rows = await session.QueryAsync(sql, parameters, cancellationToken).ConfigureAwait(false);
        return RowMapper.MapAll<T>(rows);
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
