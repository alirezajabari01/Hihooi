namespace Hihooi.Core.Diagnostics.Health;

public enum NodeHealthStatus
{
    Unknown = 0,
    Healthy = 1,
    Degraded = 2,
    Unhealthy = 3,
    Offline = 4
}

public sealed class NodeHealthSnapshot
{
    public string NodeId { get; set; } = "";
    public string Role { get; set; } = "";
    public NodeHealthStatus Status { get; set; } = NodeHealthStatus.Unknown;
    public DateTime? LastCheckUtc { get; set; }
    public double? LatencyMs { get; set; }
    public string? LastError { get; set; }
    public DateTime? LastStatusChangeUtc { get; set; }
    public string? Server { get; set; }
    public string? Database { get; set; }
    public string? Provider { get; set; }
    public long ConsecutiveFailures { get; set; }
}

public sealed class NodeHealthHistoryEntry
{
    public DateTime TimestampUtc { get; set; }
    public string NodeId { get; set; } = "";
    public NodeHealthStatus From { get; set; }
    public NodeHealthStatus To { get; set; }
    public string? Detail { get; set; }
}

public interface INodeHealthProbe
{
    Task<(bool Ok, double LatencyMs, string? Error)> ProbeAsync(string connectionString, CancellationToken cancellationToken = default);
}

public interface INodeHealthMonitor
{
    NodeHealthSnapshot? Get(string nodeId);
    IReadOnlyList<NodeHealthSnapshot> Snapshot();
    IReadOnlyList<NodeHealthHistoryEntry> History(string? nodeId = null, int take = 100);
    NodeHealthStatus GetStatus(string nodeId);
    double? GetLatencyMs(string nodeId);
}
