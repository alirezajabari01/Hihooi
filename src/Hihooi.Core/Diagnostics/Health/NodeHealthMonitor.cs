using System.Collections.Concurrent;
using Hihooi.Core.Configuration;
using Hihooi.Core.Diagnostics.Tracing;

namespace Hihooi.Core.Diagnostics.Health;

public sealed class NodeHealthMonitor : INodeHealthMonitor, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, NodeHealthSnapshot> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<NodeHealthHistoryEntry> _history = new();
    private readonly INodeHealthProbe _probe;
    private readonly ObservabilityOptions _options;
    private readonly Func<IEnumerable<(string Id, string Role, string ConnectionString, string? Provider)>> _endpoints;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private int _disposeOnce;

    public NodeHealthMonitor(
        INodeHealthProbe probe,
        ObservabilityOptions options,
        Func<IEnumerable<(string Id, string Role, string ConnectionString, string? Provider)>> endpoints)
    {
        _probe = probe;
        _options = options;
        _endpoints = endpoints;
        _loop = Task.Run(RunAsync);
    }

    public NodeHealthSnapshot? Get(string nodeId)
        => _nodes.TryGetValue(nodeId, out var s) ? Clone(s) : null;

    public IReadOnlyList<NodeHealthSnapshot> Snapshot()
        => _nodes.Values.Select(Clone).OrderBy(n => n.NodeId, StringComparer.OrdinalIgnoreCase).ToList();

    public IReadOnlyList<NodeHealthHistoryEntry> History(string? nodeId = null, int take = 100)
    {
        take = Math.Clamp(take, 1, 500);
        IEnumerable<NodeHealthHistoryEntry> q = _history.Reverse();
        if (!string.IsNullOrWhiteSpace(nodeId))
            q = q.Where(h => string.Equals(h.NodeId, nodeId, StringComparison.OrdinalIgnoreCase));
        return q.Take(take).ToList();
    }

    public NodeHealthStatus GetStatus(string nodeId)
        => _nodes.TryGetValue(nodeId, out var s) ? s.Status : NodeHealthStatus.Unknown;

    public double? GetLatencyMs(string nodeId)
        => _nodes.TryGetValue(nodeId, out var s) ? s.LatencyMs : null;

    private async Task RunAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    foreach (var ep in _endpoints())
                        await ProbeOneAsync(ep.Id, ep.Role, ep.ConnectionString, ep.Provider, _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch { /* isolate */ }

                try { await Task.Delay(_options.HealthProbeInterval, _cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
            }
        }
        catch (ObjectDisposedException) { /* shutting down */ }
    }

    private async Task ProbeOneAsync(string id, string role, string cs, string? provider, CancellationToken ct)
    {
        var (server, database) = TraceMasking.ParseEndpoint(cs);
        var snap = _nodes.GetOrAdd(id, _ => new NodeHealthSnapshot
        {
            NodeId = id,
            Role = role,
            Provider = provider,
            Server = server,
            Database = database,
            Status = NodeHealthStatus.Unknown
        });

        var swOk = false;
        double latency = 0;
        string? error = null;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(_options.HealthProbeTimeout);
            var result = await _probe.ProbeAsync(cs, linked.Token).ConfigureAwait(false);
            swOk = result.Ok;
            latency = result.LatencyMs;
            error = result.Error;
        }
        catch (Exception ex)
        {
            swOk = false;
            error = ex.Message;
        }

        lock (snap)
        {
            snap.LastCheckUtc = DateTime.UtcNow;
            snap.Server = server;
            snap.Database = database;
            snap.Provider = provider;
            snap.Role = role;
            var previous = snap.Status;
            if (swOk)
            {
                snap.ConsecutiveFailures = 0;
                snap.LatencyMs = latency;
                snap.LastError = null;
                snap.Status = latency >= _options.HealthDegradedLatencyMs
                    ? NodeHealthStatus.Degraded
                    : NodeHealthStatus.Healthy;
            }
            else
            {
                snap.ConsecutiveFailures++;
                snap.LastError = error;
                snap.LatencyMs = latency > 0 ? latency : null;
                snap.Status = snap.ConsecutiveFailures >= _options.HealthUnhealthyFailureThreshold
                    ? NodeHealthStatus.Unhealthy
                    : NodeHealthStatus.Degraded;
                if (error is not null && error.Contains("offline", StringComparison.OrdinalIgnoreCase))
                    snap.Status = NodeHealthStatus.Offline;
            }

            if (previous != snap.Status)
            {
                snap.LastStatusChangeUtc = DateTime.UtcNow;
                _history.Enqueue(new NodeHealthHistoryEntry
                {
                    TimestampUtc = DateTime.UtcNow,
                    NodeId = id,
                    From = previous,
                    To = snap.Status,
                    Detail = error
                });
                while (_history.Count > 1000 && _history.TryDequeue(out _)) { }
            }
        }
    }

    private static NodeHealthSnapshot Clone(NodeHealthSnapshot s)
    {
        lock (s)
        {
            return new NodeHealthSnapshot
            {
                NodeId = s.NodeId,
                Role = s.Role,
                Status = s.Status,
                LastCheckUtc = s.LastCheckUtc,
                LatencyMs = s.LatencyMs,
                LastError = s.LastError,
                LastStatusChangeUtc = s.LastStatusChangeUtc,
                Server = s.Server,
                Database = s.Database,
                Provider = s.Provider,
                ConsecutiveFailures = s.ConsecutiveFailures
            };
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeOnce, 1) != 0)
            return;

        try { _cts.Cancel(); } catch (ObjectDisposedException) { }

        try { await _loop.ConfigureAwait(false); }
        catch { /* ignore */ }

        _cts.Dispose();
    }
}
