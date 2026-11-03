using System.Text.Json.Serialization;

namespace Hihooi.Core.Diagnostics.Tracing;

public enum SpanStatus
{
    Ok = 0,
    Error = 1,
    Unset = 2
}

public enum RequestKind
{
    Unknown = 0,
    Read = 1,
    Write = 2,
    ReplicationApply = 3,
    HealthCheck = 4
}

public sealed class HihooiSpan
{
    public string SpanId { get; set; } = "";
    public string? ParentSpanId { get; set; }
    public string Name { get; set; } = "";
    public string Component { get; set; } = "";
    public DateTime StartUtc { get; set; }
    public DateTime? EndUtc { get; set; }
    public double? DurationMs { get; set; }
    public SpanStatus Status { get; set; } = SpanStatus.Unset;
    public string? ErrorMessage { get; set; }
    public string? NodeId { get; set; }
    public Dictionary<string, string?> Attributes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class RouteCandidateSnapshot
{
    public string NodeId { get; set; } = "";
    public bool Eligible { get; set; }
    public bool Selected { get; set; }
    public string? RejectReason { get; set; }
    public long AppliedTsId { get; set; }
    public int InflightReads { get; set; }
    public string HealthStatus { get; set; } = "Unknown";
    public double? HealthLatencyMs { get; set; }
}

public sealed class HihooiTrace
{
    public string TraceId { get; set; } = "";
    public string RequestId { get; set; } = "";
    public string? ParentTraceId { get; set; }
    public string Operation { get; set; } = "";
    public RequestKind Kind { get; set; }
    public DateTime StartUtc { get; set; }
    public DateTime? EndUtc { get; set; }
    public double? DurationMs { get; set; }
    public SpanStatus Status { get; set; } = SpanStatus.Unset;
    public string? ErrorMessage { get; set; }
    public string? SelectedNodeId { get; set; }
    public string? ConsistencyMode { get; set; }
    public string? Sql { get; set; }
    public string? SqlShape { get; set; }
    public Dictionary<string, string?>? Parameters { get; set; }
    public int? RowsReturned { get; set; }
    public int? RowsAffected { get; set; }
    public List<object?>? ResultSample { get; set; }
    public List<RouteCandidateSnapshot>? RoutingCandidates { get; set; }
    public string? RouteReason { get; set; }
    public string ApplicationName { get; set; } = "";
    public string? ApplicationInstance { get; set; }
    public List<HihooiSpan> Spans { get; set; } = new();
    public bool Incomplete { get; set; }

    [JsonIgnore]
    public bool IsInFlight => EndUtc is null;
}

public sealed class TraceTrafficItem
{
    public string TraceId { get; set; } = "";
    public string RequestId { get; set; } = "";
    public string Operation { get; set; } = "";
    public RequestKind Kind { get; set; }
    public string? SelectedNodeId { get; set; }
    public string? ConsistencyMode { get; set; }
    public DateTime StartUtc { get; set; }
    public double? DurationMs { get; set; }
    public SpanStatus Status { get; set; }
    public bool InFlight { get; set; }
}
