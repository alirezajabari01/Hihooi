using System.Globalization;
using System.Text.Json;
using Hihooi.AspNetCore.Hubs;
using Hihooi.Core.Configuration;
using Hihooi.Core.Diagnostics;
using Hihooi.Core.Model;
using Hihooi.Core.Diagnostics.Health;
using Hihooi.Core.Diagnostics.Tracing;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using HihooiClient = Hihooi.HihooiClient;

namespace Hihooi.AspNetCore;

public static class HihooiDashboardExtensions
{
    private static readonly JsonSerializerOptions DashboardJson = new(JsonSerializerDefaults.Web);

    public static IServiceCollection AddHihooiDashboard(this IServiceCollection services)
    {
        services
            .AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, DashboardAuthHandler>(
                DashboardAuthConstants.Scheme, _ => { });
        services.AddAuthorization(o =>
        {
            o.AddPolicy(DashboardAuthConstants.Policy, p =>
                p.AddAuthenticationSchemes(DashboardAuthConstants.Scheme).RequireAuthenticatedUser());
        });
        services.AddSignalR();
        return services;
    }

    public static IEndpointRouteBuilder MapHihooiDashboard(this IEndpointRouteBuilder endpoints)
    {
        var sp = endpoints.ServiceProvider;
        var options = sp.GetRequiredService<HihooiOptions>();
        if (!options.EnableDashboard)
            return endpoints;

        var schemes = sp.GetService<IAuthenticationSchemeProvider>();
        if (schemes is null)
            throw new InvalidOperationException("Call AddHihooiDashboard() before MapHihooiDashboard().");

        var path = NormalizePath(options.DashboardPath);
        var group = endpoints.MapGroup(path);
        group.RequireAuthorization(DashboardAuthConstants.Policy);

        group.MapGet("/", async (HttpContext ctx) =>
        {
            var html = await LoadIndexHtmlAsync(path).ConfigureAwait(false);
            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.WriteAsync(html).ConfigureAwait(false);
        });

        group.MapGet("/api/metrics", (HihooiClient client) => JsonBody(BuildMetrics(client)));
        group.MapGet("/api/overview", (HihooiClient client, IHihooiTracer tracer, INodeHealthMonitor? health) =>
            JsonBody(BuildOverview(client, tracer, health)));
        group.MapGet("/api/topology", (HihooiClient client, INodeHealthMonitor? health) =>
            JsonBody(BuildTopology(client, health)));
        group.MapGet("/api/nodes", (HihooiClient client, INodeHealthMonitor? health) =>
            JsonBody(BuildNodes(client, health)));
        group.MapGet("/api/nodes/{id}", (string id, HihooiClient client, INodeHealthMonitor? health) =>
        {
            var node = BuildNodes(client, health)
                .FirstOrDefault(n => string.Equals(n.Id, id, StringComparison.OrdinalIgnoreCase));
            return node is null
                ? Results.NotFound(new { available = false, reason = "Node not found" })
                : JsonBody(node);
        });
        group.MapGet("/api/health", (INodeHealthMonitor? health) =>
            JsonBody(new
            {
                available = health is not null,
                nodes = health?.Snapshot() ?? Array.Empty<NodeHealthSnapshot>(),
                history = health?.History(take: 100) ?? Array.Empty<NodeHealthHistoryEntry>()
            }));
        group.MapGet("/api/traffic", (
            IHihooiTracer tracer,
            int? take,
            string? kind,
            string? status,
            string? nodeId,
            string? consistency,
            string? traceId,
            bool? inFlight,
            double? minDurationMs) =>
        {
            RequestKind? k = Enum.TryParse<RequestKind>(kind, true, out var rk) ? rk : null;
            SpanStatus? st = Enum.TryParse<SpanStatus>(status, true, out var ss) ? ss : null;
            var items = tracer.Store.Query(
                take ?? 100, k, st, nodeId, consistency, traceId, inFlight, minDurationMs);
            return JsonBody(new
            {
                available = true,
                items = items.Select(ToTrafficDto)
            });
        });
        group.MapGet("/api/traces", (IHihooiTracer tracer, int? take) =>
            JsonBody(new { available = true, items = tracer.Store.Query(take ?? 50).Select(ToTraceSummary) }));
        group.MapGet("/api/traces/{traceId}", (string traceId, IHihooiTracer tracer) =>
        {
            var t = tracer.Store.Get(traceId);
            return t is null
                ? Results.NotFound(new { available = false, reason = "Trace not found" })
                : JsonBody(ToTraceDetail(t));
        });
        group.MapGet("/api/replication", async (HihooiClient client, IHihooiTracer tracer, CancellationToken ct) =>
            JsonBody(await BuildReplicationAsync(client, tracer, ct).ConfigureAwait(false)));
        group.MapGet("/api/analytics/queries", (IHihooiTracer tracer) =>
            JsonBody(new { available = true, analytics = tracer.Store.Analytics() }));
        group.MapGet("/api/consistency", (HihooiClient client) => JsonBody(BuildConsistency(client)));
        group.MapGet("/api/connections", (HihooiClient client) =>
        {
            var snap = client.GetMetricsSnapshot();
            return JsonBody(new
            {
                available = true,
                extensions = snap.Extensions.Select(e => new
                {
                    e.Id,
                    e.InflightReads,
                    e.IsOnline,
                    e.ApplyFailures
                })
            });
        });
        group.MapGet("/api/events", (HihooiClient client, INodeHealthMonitor? health, int? take) =>
            JsonBody(new
            {
                available = true,
                engine = client.GetRecentEvents(take ?? 200),
                health = health?.History(take: take ?? 100) ?? Array.Empty<NodeHealthHistoryEntry>(),
                errors = SanitizeErrors(client.GetMetricsSnapshot().RecentErrors)
            }));
        group.MapGet("/api/config", (HihooiOptions opt) => JsonBody(BuildConfig(opt)));

        group.MapHub<MetricsHub>("/hubs/metrics");

        var lifetime = sp.GetService<IHostApplicationLifetime>();
        var hub = sp.GetService<IHubContext<MetricsHub>>();
        if (lifetime is not null && hub is not null)
        {
            _ = Task.Run(async () =>
            {
                var ct = lifetime.ApplicationStopping;
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        var client = sp.GetService<HihooiClient>();
                        var tracer = sp.GetService<IHihooiTracer>();
                        object metrics = client is null
                            ? new { available = false, reason = "HihooiClient Not Available" }
                            : BuildMetrics(client);
                        await hub.Clients.All.SendAsync("metrics", metrics, ct).ConfigureAwait(false);
                        if (tracer is not null)
                        {
                            await hub.Clients.All.SendAsync("traffic", new
                            {
                                available = true,
                                items = tracer.Store.RecentTraffic(40)
                            }, ct).ConfigureAwait(false);
                        }
                    }
                    catch
                    {
                        await hub.Clients.All.SendAsync("metrics",
                            new { available = false, reason = "Not Available" }, ct).ConfigureAwait(false);
                    }
                    try { await Task.Delay(1000, ct).ConfigureAwait(false); } catch { break; }
                }
            });
        }

        return endpoints;
    }

    private static IResult JsonBody(object payload) => Results.Json(payload, DashboardJson);

    private static object BuildOverview(HihooiClient client, IHihooiTracer tracer, INodeHealthMonitor? health)
    {
        var snap = client.GetMetricsSnapshot();
        var analytics = tracer.Store.Analytics();
        var nodes = health?.Snapshot() ?? Array.Empty<NodeHealthSnapshot>();
        return new
        {
            available = true,
            startedUtc = snap.StartedUtc,
            consistencyMode = client.Options.ConsistencyMode.ToString(),
            latestTsId = client.LatestCommittedTsId,
            commits = snap.Commits,
            rollbacks = snap.Rollbacks,
            failedApplies = snap.FailedApplies,
            primaryReads = snap.PrimaryReads,
            extensionReads = snap.ExtensionReads,
            primaryFallbacks = snap.PrimaryFallbacks,
            nodesOnline = nodes.Count(n => n.Status is NodeHealthStatus.Healthy or NodeHealthStatus.Degraded),
            nodesTotal = Math.Max(nodes.Count, 1 + client.Options.Extensions.Count),
            analytics
        };
    }

    private static object BuildMetrics(HihooiClient client)
    {
        var snap = client.GetMetricsSnapshot();
        var journal = JournalStatus(client.Options);
        return new
        {
            available = true,
            startedUtc = snap.StartedUtc,
            consistencyMode = client.Options.ConsistencyMode.ToString(),
            commits = snap.Commits,
            rollbacks = snap.Rollbacks,
            failedApplies = snap.FailedApplies,
            primaryReads = snap.PrimaryReads,
            extensionReads = snap.ExtensionReads,
            primaryFallbacks = snap.PrimaryFallbacks,
            conflictsDetected = snap.ConflictsDetected,
            parallelAppliesStarted = snap.ParallelAppliesStarted,
            latestTsId = client.LatestCommittedTsId,
            journal = new { configured = journal.Configured, available = journal.Available, note = journal.Note },
            extensions = LiveExtensions(snap),
            readsByExtension = snap.ReadsByExtension,
            routeReasons = snap.RouteReasons,
            errors = SanitizeErrors(snap.RecentErrors)
        };
    }

    private static object BuildTopology(HihooiClient client, INodeHealthMonitor? health)
        => new { available = true, nodes = BuildNodes(client, health) };

    private sealed class NodeDto
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Role { get; init; } = "";
        public string Provider { get; init; } = "";
        public string? Server { get; init; }
        public string? Database { get; init; }
        public string HealthStatus { get; init; } = "Unknown";
        public DateTime? LastHealthCheckUtc { get; init; }
        public double? LatencyMs { get; init; }
        public bool IsOnline { get; init; }
        public long? AppliedTsId { get; init; }
        public long? Lag { get; init; }
        public int InflightReads { get; init; }
        public long ApplyFailures { get; init; }
        public long SuccessfulReads { get; init; }
        public string? LastError { get; init; }
        public DateTime? LastStatusChangeUtc { get; init; }
        public IReadOnlyList<NodeHealthHistoryEntry>? History { get; init; }
    }

    private static List<NodeDto> BuildNodes(HihooiClient client, INodeHealthMonitor? health)
    {
        var snap = client.GetMetricsSnapshot();
        var list = new List<NodeDto>();
        var primaryHealth = health?.Get("primary");
        list.Add(new NodeDto
        {
            Id = "primary",
            Name = "primary",
            Role = "Primary",
            Provider = client.Options.ProviderKind.ToString(),
            Server = primaryHealth?.Server,
            Database = primaryHealth?.Database,
            HealthStatus = primaryHealth?.Status.ToString() ?? "Unknown",
            LastHealthCheckUtc = primaryHealth?.LastCheckUtc,
            LatencyMs = primaryHealth?.LatencyMs,
            IsOnline = true,
            AppliedTsId = client.LatestCommittedTsId,
            Lag = 0,
            InflightReads = 0,
            ApplyFailures = 0,
            SuccessfulReads = snap.PrimaryReads,
            LastError = primaryHealth?.LastError,
            LastStatusChangeUtc = primaryHealth?.LastStatusChangeUtc,
            History = health?.History("primary", 50)
        });

        foreach (var configured in client.Options.Extensions)
        {
            var live = snap.Extensions.FirstOrDefault(e =>
                string.Equals(e.Id, configured.Id, StringComparison.OrdinalIgnoreCase));
            var h = health?.Get(configured.Id);
            list.Add(new NodeDto
            {
                Id = configured.Id,
                Name = configured.Id,
                Role = "Extension",
                Provider = client.Options.ProviderKind.ToString(),
                Server = h?.Server,
                Database = h?.Database,
                HealthStatus = h?.Status.ToString() ?? (live?.IsOnline == true ? "Unknown" : "Offline"),
                LastHealthCheckUtc = h?.LastCheckUtc,
                LatencyMs = h?.LatencyMs,
                IsOnline = live?.IsOnline ?? false,
                AppliedTsId = live?.AppliedTsId,
                Lag = live?.Lag,
                InflightReads = live?.InflightReads ?? 0,
                ApplyFailures = live?.ApplyFailures ?? 0,
                SuccessfulReads = snap.ReadsByExtension.TryGetValue(configured.Id, out var r) ? r : 0L,
                LastError = h?.LastError,
                LastStatusChangeUtc = h?.LastStatusChangeUtc,
                History = health?.History(configured.Id, 50)
            });
        }

        return list;
    }

    private static async Task<object> BuildReplicationAsync(HihooiClient client, IHihooiTracer tracer, CancellationToken ct)
    {
        var snap = client.GetMetricsSnapshot();
        var applies = tracer.Store.Query(take: 200, kind: RequestKind.ReplicationApply);
        var latest = client.LatestCommittedTsId;
        var nodes = new List<object>();
        foreach (var configured in client.Options.Extensions)
        {
            var live = snap.Extensions.FirstOrDefault(e =>
                string.Equals(e.Id, configured.Id, StringComparison.OrdinalIgnoreCase));
            var applied = live?.AppliedTsId ?? 0;
            var lag = Math.Max(0, latest - applied);
            IReadOnlyList<ReplicationLogEntry> pending;
            try
            {
                pending = lag == 0
                    ? Array.Empty<ReplicationLogEntry>()
                    : await client.GetUnappliedLogAsync(configured.Id, 40, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                pending = new[]
                {
                    new ReplicationLogEntry { TsId = 0, Sql = "Log read failed: " + ex.Message, Completion = "Error" }
                };
            }

            nodes.Add(new
            {
                id = configured.Id,
                isOnline = live?.IsOnline ?? false,
                appliedTsId = applied,
                latestTsId = latest,
                lag,
                inSync = lag == 0 && (live?.IsOnline ?? false),
                syncStatus = !(live?.IsOnline ?? false) ? "Offline" : lag == 0 ? "InSync" : "Behind",
                inflightReads = live?.InflightReads ?? 0,
                applyFailures = live?.ApplyFailures ?? 0,
                pendingLogs = pending.Select(p => new
                {
                    tsId = p.TsId,
                    sql = p.Sql,
                    commitTimestampUtc = p.CommitTimestampUtc,
                    completion = p.Completion,
                    isFailed = p.IsFailed
                })
            });
        }

        return new
        {
            available = true,
            latestTsId = latest,
            pendingEstimate = client.PendingPublishedEstimate,
            failedApplies = snap.FailedApplies,
            successfulApplies = applies.Count(a => a.Status == SpanStatus.Ok),
            failedApplyTraces = applies.Count(a => a.Status == SpanStatus.Error),
            extensions = nodes,
            recentApplies = applies.Take(40).Select(ToTrafficDto)
        };
    }

    private static object BuildConsistency(HihooiClient client)
    {
        var snap = client.GetMetricsSnapshot();
        return new
        {
            available = true,
            consistencyMode = client.Options.ConsistencyMode.ToString(),
            primaryReads = snap.PrimaryReads,
            extensionReads = snap.ExtensionReads,
            primaryFallbacks = snap.PrimaryFallbacks,
            conflictsDetected = snap.ConflictsDetected,
            readsByExtension = snap.ReadsByExtension,
            routeReasons = snap.RouteReasons
        };
    }

    private static object BuildConfig(HihooiOptions opt) => new
    {
        available = true,
        provider = opt.ProviderKind.ToString(),
        consistencyMode = opt.ConsistencyMode.ToString(),
        extensionCount = opt.Extensions.Count,
        extensionIds = opt.Extensions.Select(e => e.Id).ToArray(),
        ensureSnapshotIsolationOnStart = opt.EnsureSnapshotIsolationOnStart,
        redisConfigured = !string.IsNullOrWhiteSpace(opt.RedisConnectionString),
        recoveryJournalConfigured = !string.IsNullOrWhiteSpace(opt.RecoveryJournalDirectory),
        dashboardPath = opt.DashboardPath,
        observability = new
        {
            opt.Observability.Enabled,
            opt.Observability.SamplingRate,
            opt.Observability.CaptureSql,
            opt.Observability.CaptureParameters,
            opt.Observability.MaskSensitiveParameters,
            opt.Observability.MaxResultSampleRows,
            durableStorage = !string.IsNullOrWhiteSpace(opt.Observability.StorageDirectory),
            opt.Observability.ApplicationName
        }
    };

    private static object ToTrafficDto(HihooiTrace t) => new
    {
        traceId = t.TraceId,
        requestId = t.RequestId,
        operation = t.Operation,
        kind = t.Kind.ToString(),
        selectedNodeId = t.SelectedNodeId,
        consistencyMode = t.ConsistencyMode,
        startUtc = t.StartUtc,
        durationMs = t.DurationMs,
        status = t.Status.ToString(),
        inFlight = t.EndUtc is null,
        errorMessage = t.ErrorMessage
    };

    private static object ToTraceSummary(HihooiTrace t) => new
    {
        traceId = t.TraceId,
        requestId = t.RequestId,
        operation = t.Operation,
        kind = t.Kind.ToString(),
        selectedNodeId = t.SelectedNodeId,
        consistencyMode = t.ConsistencyMode,
        startUtc = t.StartUtc,
        durationMs = t.DurationMs,
        status = t.Status.ToString(),
        spanCount = t.Spans.Count,
        incomplete = t.Incomplete
    };

    private static object ToTraceDetail(HihooiTrace t) => new
    {
        available = true,
        incomplete = t.Incomplete,
        traceId = t.TraceId,
        requestId = t.RequestId,
        parentTraceId = t.ParentTraceId,
        operation = t.Operation,
        kind = t.Kind.ToString(),
        startUtc = t.StartUtc,
        endUtc = t.EndUtc,
        durationMs = t.DurationMs,
        status = t.Status.ToString(),
        errorMessage = t.ErrorMessage,
        selectedNodeId = t.SelectedNodeId,
        consistencyMode = t.ConsistencyMode,
        routeReason = t.RouteReason,
        routingCandidates = t.RoutingCandidates,
        sql = t.Sql,
        sqlShape = t.SqlShape,
        parameters = t.Parameters,
        rowsReturned = t.RowsReturned,
        rowsAffected = t.RowsAffected,
        resultSample = t.ResultSample,
        applicationName = t.ApplicationName,
        applicationInstance = t.ApplicationInstance,
        spanCount = t.Spans.Count,
        errorSpanCount = t.Spans.Count(s => s.Status == SpanStatus.Error),
        spans = t.Spans.Select(s => new
        {
            spanId = s.SpanId,
            parentSpanId = s.ParentSpanId,
            name = s.Name,
            component = s.Component,
            startUtc = s.StartUtc,
            endUtc = s.EndUtc,
            durationMs = s.DurationMs,
            status = s.Status.ToString(),
            errorMessage = s.ErrorMessage,
            nodeId = s.NodeId,
            attributes = s.Attributes
        })
    };

    private static object[] LiveExtensions(HihooiMetricsSnapshot snap) =>
        snap.Extensions
            .OrderBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
            .Select(e => (object)new
            {
                id = e.Id,
                reported = true,
                isOnline = (bool?)e.IsOnline,
                appliedTsId = (long?)e.AppliedTsId,
                lag = (long?)e.Lag,
                inflightReads = (int?)e.InflightReads,
                applyFailures = (long?)e.ApplyFailures
            })
            .ToArray();

    private static (bool Configured, bool Available, string Note) JournalStatus(HihooiOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.RecoveryJournalDirectory))
            return (false, false, "No recovery journal folder is configured.");
        bool available;
        try { available = Directory.Exists(options.RecoveryJournalDirectory); }
        catch { available = false; }
        return available
            ? (true, true, "Journal folder is configured and reachable from this process.")
            : (true, false, "A journal folder is configured, but this process cannot see it on disk.");
    }

    internal static IReadOnlyList<string> SanitizeErrors(IReadOnlyList<string> errors)
    {
        static string Redact(string line)
        {
            var s = System.Text.RegularExpressions.Regex.Replace(
                line,
                @"(Password|Pwd|AccountKey|SharedAccessKey|ApiKey|Secret)\s*=\s*[^;,\s]+",
                "$1=***",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            s = System.Text.RegularExpressions.Regex.Replace(
                s,
                @"(Server|Data Source)\s*=\s*[^;]+",
                "$1=***",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return s;
        }
        return errors.Select(Redact).ToArray();
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "/hihooi";
        path = path.Trim();
        if (!path.StartsWith('/')) path = "/" + path;
        return path.TrimEnd('/');
    }

    private static async Task<string> LoadIndexHtmlAsync(string basePath)
    {
        var asm = typeof(HihooiDashboardExtensions).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("wwwroot.index.html", StringComparison.OrdinalIgnoreCase)
                                 || n.EndsWith("index.html", StringComparison.OrdinalIgnoreCase));
        if (name is null)
            return "<html><body>Hihooi dashboard resource missing.</body></html>";

        await using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        var html = await reader.ReadToEndAsync().ConfigureAwait(false);
        return html.Replace("__HIHOOI_BASE__", basePath, StringComparison.Ordinal);
    }
}
