namespace Hihooi;

/// <summary>
/// Primary public API for Hihooi consumers. All SQL goes through the middleware engine
/// (routing, consistency, replication, metrics/events). Do not open sessions manually.
/// </summary>
public interface IHihooiDatabase
{
    /// <summary>Execute a write statement (INSERT/UPDATE/DELETE/DDL). Returns affected rows.</summary>
    Task<int> ExecuteAsync(
        string sql,
        object? parameters = null,
        CancellationToken cancellationToken = default);

    /// <summary>Execute a read query and map each row to <typeparamref name="T"/>.</summary>
    Task<IReadOnlyList<T>> QueryAsync<T>(
        string sql,
        object? parameters = null,
        CancellationToken cancellationToken = default);

    /// <summary>Execute a read query; returns the first row or default.</summary>
    Task<T?> QueryFirstOrDefaultAsync<T>(
        string sql,
        object? parameters = null,
        CancellationToken cancellationToken = default);

    /// <summary>Execute a read query; expects exactly one row.</summary>
    Task<T> QuerySingleAsync<T>(
        string sql,
        object? parameters = null,
        CancellationToken cancellationToken = default);
}
