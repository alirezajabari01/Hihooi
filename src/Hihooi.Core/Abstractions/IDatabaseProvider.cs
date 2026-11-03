namespace Hihooi.Core.Abstractions;

public interface IDatabaseProvider
{
    string ProviderName { get; }
    Task<IDatabaseSession> OpenSessionAsync(string connectionString, string replicaId, CancellationToken cancellationToken = default);
    Task EnsureSnapshotIsolationAsync(string connectionString, CancellationToken cancellationToken = default);
}
