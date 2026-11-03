using Hihooi.Core.Consistency;

namespace Hihooi.Core.Configuration;

public sealed class HihooiOptions
{
    /// <summary>Database provider. Defaults to SqlServer when left unspecified at registration time.</summary>
    public HihooiDatabaseProviderKind ProviderKind { get; set; } = HihooiDatabaseProviderKind.Unspecified;

    public string PrimaryConnectionString { get; set; } = "";
    public List<ExtensionEndpoint> Extensions { get; set; } = new();
    public ConsistencyLevel DefaultConsistencyLevel { get; set; } = ConsistencyLevel.Gssi;
    /// <summary>Alias for consumers expecting ConsistencyMode naming.</summary>
    public ConsistencyLevel ConsistencyMode
    {
        get => DefaultConsistencyLevel;
        set => DefaultConsistencyLevel = value;
    }
    public TimeSpan RsiPcWaitTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public bool EnsureSnapshotIsolationOnStart { get; set; } = true;
    /// <summary>Optional directory for file recovery journal. Null disables journaling.</summary>
    public string? RecoveryJournalDirectory { get; set; }
    /// <summary>Optional Redis connection for distributed buffer (null = in-memory).</summary>
    public string? RedisConnectionString { get; set; }
    public string RedisKeyPrefix { get; set; } = "hihooi:";
    public int CommandTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// When true, ASP.NET Core hosts may map the embedded dashboard via MapHihooiDashboard.
    /// Console/Worker hosts ignore this unless they host ASP.NET Core.
    /// </summary>
    public bool EnableDashboard { get; set; }

    /// <summary>Dashboard URL path prefix. Default: /hihooi</summary>
    public string DashboardPath { get; set; } = "/hihooi";

    public DashboardAuthOptions DashboardAuth { get; set; } = new();

    /// <summary>Request tracing, health probes, and dashboard telemetry.</summary>
    public ObservabilityOptions Observability { get; set; } = new();
}

/// <summary>Authentication / authorization for the embedded dashboard.</summary>
public sealed class DashboardAuthOptions
{
    /// <summary>Require authentication for dashboard UI and APIs. Default true.</summary>
    public bool RequireAuthorization { get; set; } = true;

    /// <summary>
    /// Optional ASP.NET authorization policy name. When null and RequireAuthorization is true,
    /// the default authorize policy is used (authenticated user).
    /// </summary>
    public string? AuthorizationPolicy { get; set; }

    /// <summary>
    /// Optional shared API key. When set, requests with matching header are allowed
    /// even without a user principal (useful for local ops). Leave null to disable.
    /// </summary>
    public string? ApiKey { get; set; }

    public string ApiKeyHeaderName { get; set; } = "X-Hihooi-Dashboard-Key";
}

public sealed class ExtensionEndpoint
{
    public string Id { get; set; } = "";
    public string ConnectionString { get; set; } = "";
}
