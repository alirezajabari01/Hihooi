namespace Hihooi.Core.Abstractions;

public interface IDatabaseSession : IAsyncDisposable, IDisposable
{
    string ReplicaId { get; }
    Task BeginTransactionAsync(CancellationToken cancellationToken = default);
    Task CommitAsync(CancellationToken cancellationToken = default);
    Task RollbackAsync(CancellationToken cancellationToken = default);
    Task<int> ExecuteNonQueryAsync(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteReaderAsync(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken cancellationToken = default);
    Task<DateTime> GetEngineUtcTimestampAsync(CancellationToken cancellationToken = default);
}
