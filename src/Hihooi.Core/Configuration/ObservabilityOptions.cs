namespace Hihooi.Core.Configuration;

/// <summary>Request tracing, sampling, masking, and durable storage for the dashboard.</summary>
public sealed class ObservabilityOptions
{
    /// <summary>When false, tracer is a no-op (zero overhead beyond a bool check).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>0..1 probability a new root request is recorded. Default 1.0.</summary>
    public double SamplingRate { get; set; } = 1.0;

    public bool CaptureSql { get; set; } = true;
    public bool CaptureParameters { get; set; } = true;
    public bool MaskSensitiveParameters { get; set; } = true;
    public int MaxResultSampleRows { get; set; } = 5;
    public int MaxAttributeBytes { get; set; } = 16_384;
    public int ChannelCapacity { get; set; } = 4096;
    public int InMemoryCapacity { get; set; } = 2000;
    public TimeSpan Retention { get; set; } = TimeSpan.FromHours(24);

    /// <summary>When set, completed traces are also written as JSONL and reloaded after restart.</summary>
    public string? StorageDirectory { get; set; }

    public string ApplicationName { get; set; } = "Hihooi";
    public string? ApplicationInstance { get; set; }

    public TimeSpan HealthProbeInterval { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan HealthProbeTimeout { get; set; } = TimeSpan.FromSeconds(2);
    public int HealthUnhealthyFailureThreshold { get; set; } = 3;
    public int HealthDegradedLatencyMs { get; set; } = 200;
}
