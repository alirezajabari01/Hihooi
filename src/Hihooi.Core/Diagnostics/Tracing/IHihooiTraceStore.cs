namespace Hihooi.Core.Diagnostics.Tracing;

public interface IHihooiTraceStore
{
    void UpsertInFlight(HihooiTrace trace);
    void Complete(HihooiTrace trace);
    HihooiTrace? Get(string traceId);
    IReadOnlyList<HihooiTrace> Query(
        int take = 100,
        RequestKind? kind = null,
        SpanStatus? status = null,
        string? nodeId = null,
        string? consistencyMode = null,
        string? traceIdContains = null,
        bool? inFlight = null,
        double? minDurationMs = null);
    IReadOnlyList<TraceTrafficItem> RecentTraffic(int take = 100);
    TraceAnalyticsSnapshot Analytics();
}

public sealed class TraceAnalyticsSnapshot
{
    public long TotalCompleted { get; init; }
    public long Failed { get; init; }
    public double? AvgDurationMs { get; init; }
    public double? P50Ms { get; init; }
    public double? P95Ms { get; init; }
    public double? P99Ms { get; init; }
    public IReadOnlyList<QueryShapeStat> SlowQueries { get; init; } = Array.Empty<QueryShapeStat>();
    public IReadOnlyList<QueryShapeStat> MostExecuted { get; init; } = Array.Empty<QueryShapeStat>();
    public IReadOnlyList<QueryShapeStat> FailedQueries { get; init; } = Array.Empty<QueryShapeStat>();
}

public sealed class QueryShapeStat
{
    public string SqlShape { get; init; } = "";
    public long Count { get; init; }
    public long Failures { get; init; }
    public double AvgMs { get; init; }
    public double? P95Ms { get; init; }
    public string? SampleTraceId { get; init; }
}
