using System.ComponentModel;
using Hihooi.Core.Configuration;
using Hihooi.Core.Consistency;
using Hihooi.Core.Diagnostics;
using Hihooi.Core.Model;
using Hihooi.Core.TransactionManager;

namespace Hihooi;

/// <summary>
/// Lower-level engine host used by the dashboard and advanced scenarios.
/// Application code should inject <see cref="IHihooiDatabase"/> instead.
/// </summary>
/// <remarks>Prefer <see cref="IHihooiDatabase"/> for application SQL.</remarks>
[EditorBrowsable(EditorBrowsableState.Advanced)]
public sealed class HihooiClient : IAsyncDisposable
{
    private readonly HihooiTransactionManager _tm;

    public HihooiClient(HihooiTransactionManager transactionManager)
    {
        _tm = transactionManager;
    }

    public HihooiOptions Options => _tm.Options;
    public HihooiMetrics Metrics => _tm.Metrics;
    public HihooiEventStore Events => _tm.Events;
    public long LatestCommittedTsId => _tm.LatestTsId;
    public long PendingPublishedEstimate => _tm.PendingPublishedEstimate;

    public Task StartAsync(CancellationToken cancellationToken = default)
        => _tm.StartAsync(cancellationToken);

    [EditorBrowsable(EditorBrowsableState.Never)]
    public HihooiSession OpenSession(ConsistencyLevel? consistency = null)
        => _tm.OpenSession(consistency);

    [Obsolete("Use IHihooiDatabase.QueryAsync instead.")]
    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteAsync(
        string sql,
        CancellationToken cancellationToken = default)
    {
        await using var session = OpenSession();
        return await session.QueryAsync(sql, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    [Obsolete("Use IHihooiDatabase.QueryAsync instead.")]
    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteAsync(
        string sql,
        object? parameters,
        CancellationToken cancellationToken = default)
    {
        await using var session = OpenSession();
        return await session.QueryAsync(sql, parameters, cancellationToken).ConfigureAwait(false);
    }

    [Obsolete("Use IHihooiDatabase.ExecuteAsync instead.")]
    public async Task<int> ExecuteNonQueryAsync(
        string sql,
        object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        await using var session = OpenSession();
        return await session.ExecuteNonQueryAsync(sql, parameters, cancellationToken).ConfigureAwait(false);
    }

    public void AddExtension(string id, string connectionString)
        => _tm.AddExtension(id, connectionString);

    public void MarkExtensionOffline(string id) => _tm.MarkExtensionOffline(id);
    public void MarkExtensionOnline(string id) => _tm.MarkExtensionOnline(id);

    public HihooiMetricsSnapshot GetMetricsSnapshot() => _tm.Metrics.Snapshot();

    public Task<IReadOnlyList<ReplicationLogEntry>> GetUnappliedLogAsync(
        string extensionId,
        int take = 50,
        CancellationToken cancellationToken = default)
        => _tm.GetUnappliedLogAsync(extensionId, take, cancellationToken);

    public IReadOnlyList<HihooiEvent> GetRecentEvents(int take = 200, string? typeFilter = null)
        => _tm.Events.Snapshot(take, typeFilter);

    /// <summary>
    /// No-op under DI: <see cref="HihooiTransactionManager"/> is a separate singleton owned by the container.
    /// Disposing here would double-dispose extractors/CTS when the host shuts down.
    /// </summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
