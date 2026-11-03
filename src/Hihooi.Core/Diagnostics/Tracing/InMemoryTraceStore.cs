using System.Collections.Concurrent;

namespace Hihooi.Core.Diagnostics.Tracing;

public sealed class InMemoryTraceStore : IHihooiTraceStore
{
    private readonly ConcurrentDictionary<string, HihooiTrace> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> _order = new();
    private readonly int _capacity;
    private readonly TimeSpan _retention;
    private long _completed;

    public InMemoryTraceStore(int capacity = 2000, TimeSpan? retention = null)
    {
        _capacity = Math.Max(100, capacity);
        _retention = retention ?? TimeSpan.FromHours(24);
    }

    public void UpsertInFlight(HihooiTrace trace)
    {
        _byId[trace.TraceId] = Clone(trace);
        TouchOrder(trace.TraceId);
        Trim();
    }

    public void Complete(HihooiTrace trace)
    {
        _byId[trace.TraceId] = Clone(trace);
        TouchOrder(trace.TraceId);
        Interlocked.Increment(ref _completed);
        Trim();
    }

    public HihooiTrace? Get(string traceId)
        => _byId.TryGetValue(traceId, out var t) ? Clone(t) : null;

    public IReadOnlyList<HihooiTrace> Query(
        int take = 100,
        RequestKind? kind = null,
        SpanStatus? status = null,
        string? nodeId = null,
        string? consistencyMode = null,
        string? traceIdContains = null,
        bool? inFlight = null,
        double? minDurationMs = null)
    {
        take = Math.Clamp(take, 1, _capacity);
        Expire();
        IEnumerable<HihooiTrace> q = _byId.Values.OrderByDescending(t => t.StartUtc);
        if (kind is not null) q = q.Where(t => t.Kind == kind);
        if (status is not null) q = q.Where(t => t.Status == status);
        if (!string.IsNullOrWhiteSpace(nodeId))
            q = q.Where(t => string.Equals(t.SelectedNodeId, nodeId, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(consistencyMode))
            q = q.Where(t => string.Equals(t.ConsistencyMode, consistencyMode, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(traceIdContains))
            q = q.Where(t => t.TraceId.Contains(traceIdContains, StringComparison.OrdinalIgnoreCase));
        if (inFlight is true) q = q.Where(t => t.EndUtc is null);
        if (inFlight is false) q = q.Where(t => t.EndUtc is not null);
        if (minDurationMs is not null) q = q.Where(t => (t.DurationMs ?? 0) >= minDurationMs);
        return q.Take(take).Select(Clone).ToList();
    }

    public IReadOnlyList<TraceTrafficItem> RecentTraffic(int take = 100)
    {
        return Query(take).Select(t => new TraceTrafficItem
        {
            TraceId = t.TraceId,
            RequestId = t.RequestId,
            Operation = t.Operation,
            Kind = t.Kind,
            SelectedNodeId = t.SelectedNodeId,
            ConsistencyMode = t.ConsistencyMode,
            StartUtc = t.StartUtc,
            DurationMs = t.DurationMs,
            Status = t.Status,
            InFlight = t.EndUtc is null
        }).ToList();
    }

    public TraceAnalyticsSnapshot Analytics()
    {
        Expire();
        var completed = _byId.Values.Where(t => t.EndUtc is not null && t.Kind is RequestKind.Read or RequestKind.Write).ToList();
        var durations = completed.Where(t => t.DurationMs is not null).Select(t => t.DurationMs!.Value).OrderBy(x => x).ToList();
        var byShape = completed
            .Where(t => !string.IsNullOrWhiteSpace(t.SqlShape))
            .GroupBy(t => t.SqlShape!, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var ms = g.Where(x => x.DurationMs is not null).Select(x => x.DurationMs!.Value).OrderBy(x => x).ToList();
                return new QueryShapeStat
                {
                    SqlShape = g.Key,
                    Count = g.Count(),
                    Failures = g.Count(x => x.Status == SpanStatus.Error),
                    AvgMs = ms.Count == 0 ? 0 : ms.Average(),
                    P95Ms = Percentile(ms, 0.95),
                    SampleTraceId = g.OrderByDescending(x => x.StartUtc).First().TraceId
                };
            })
            .ToList();

        return new TraceAnalyticsSnapshot
        {
            TotalCompleted = completed.Count,
            Failed = completed.Count(t => t.Status == SpanStatus.Error),
            AvgDurationMs = durations.Count == 0 ? null : durations.Average(),
            P50Ms = Percentile(durations, 0.50),
            P95Ms = Percentile(durations, 0.95),
            P99Ms = Percentile(durations, 0.99),
            SlowQueries = byShape.OrderByDescending(s => s.AvgMs).Take(20).ToList(),
            MostExecuted = byShape.OrderByDescending(s => s.Count).Take(20).ToList(),
            FailedQueries = byShape.Where(s => s.Failures > 0).OrderByDescending(s => s.Failures).Take(20).ToList()
        };
    }

    private void TouchOrder(string id) => _order.Enqueue(id);

    private void Trim()
    {
        Expire();
        while (_byId.Count > _capacity && _order.TryDequeue(out var id))
            _byId.TryRemove(id, out _);
    }

    private void Expire()
    {
        var cutoff = DateTime.UtcNow - _retention;
        foreach (var kv in _byId)
        {
            if (kv.Value.StartUtc < cutoff)
                _byId.TryRemove(kv.Key, out _);
        }
    }

    private static double? Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0) return null;
        var idx = (int)Math.Clamp(Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1);
        return sorted[idx];
    }

    private static HihooiTrace Clone(HihooiTrace t)
    {
        return new HihooiTrace
        {
            TraceId = t.TraceId,
            RequestId = t.RequestId,
            ParentTraceId = t.ParentTraceId,
            Operation = t.Operation,
            Kind = t.Kind,
            StartUtc = t.StartUtc,
            EndUtc = t.EndUtc,
            DurationMs = t.DurationMs,
            Status = t.Status,
            ErrorMessage = t.ErrorMessage,
            SelectedNodeId = t.SelectedNodeId,
            ConsistencyMode = t.ConsistencyMode,
            Sql = t.Sql,
            SqlShape = t.SqlShape,
            Parameters = t.Parameters is null ? null : new Dictionary<string, string?>(t.Parameters, StringComparer.OrdinalIgnoreCase),
            RowsReturned = t.RowsReturned,
            RowsAffected = t.RowsAffected,
            ResultSample = t.ResultSample?.ToList(),
            RoutingCandidates = t.RoutingCandidates?.Select(c => new RouteCandidateSnapshot
            {
                NodeId = c.NodeId,
                Eligible = c.Eligible,
                Selected = c.Selected,
                RejectReason = c.RejectReason,
                AppliedTsId = c.AppliedTsId,
                InflightReads = c.InflightReads,
                HealthStatus = c.HealthStatus,
                HealthLatencyMs = c.HealthLatencyMs
            }).ToList(),
            RouteReason = t.RouteReason,
            ApplicationName = t.ApplicationName,
            ApplicationInstance = t.ApplicationInstance,
            Incomplete = t.Incomplete,
            Spans = t.Spans.Select(s => new HihooiSpan
            {
                SpanId = s.SpanId,
                ParentSpanId = s.ParentSpanId,
                Name = s.Name,
                Component = s.Component,
                StartUtc = s.StartUtc,
                EndUtc = s.EndUtc,
                DurationMs = s.DurationMs,
                Status = s.Status,
                ErrorMessage = s.ErrorMessage,
                NodeId = s.NodeId,
                Attributes = new Dictionary<string, string?>(s.Attributes, StringComparer.OrdinalIgnoreCase)
            }).ToList()
        };
    }
}
