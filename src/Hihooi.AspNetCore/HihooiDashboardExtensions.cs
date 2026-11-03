using System.Text.Json;
using Hihooi.AspNetCore.Hubs;
using Hihooi.Core.Configuration;
using Hihooi.Core.Diagnostics;
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

    /// <summary>
    /// Registers dashboard auth scheme. Does not set it as the host default scheme.
    /// </summary>
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

    /// <summary>
    /// Maps embedded Hihooi dashboard under <see cref="HihooiOptions.DashboardPath"/> when EnableDashboard is true.
    /// Requires prior <see cref="AddHihooiDashboard"/>. All endpoints require <see cref="DashboardAuthConstants.Policy"/>.
    /// Payloads come from <see cref="HihooiClient.GetMetricsSnapshot"/>, latest TSID, and option names.
    /// Connection strings are never included.
    /// </summary>
    public static IEndpointRouteBuilder MapHihooiDashboard(this IEndpointRouteBuilder endpoints)
    {
        var sp = endpoints.ServiceProvider;
        var options = sp.GetRequiredService<HihooiOptions>();
        if (!options.EnableDashboard)
            return endpoints;

        if (options.DashboardAuth.RequireAuthorization
            && string.IsNullOrEmpty(options.DashboardAuth.ApiKey)
            && string.IsNullOrEmpty(options.DashboardAuth.AuthorizationPolicy))
        {
            // Still OK if host users authenticate; warn via exception only when clearly misconfigured open intent.
            // RequireAuthorization=true with no API key means "host auth only" — allowed.
        }

        // Fail closed if auth scheme was not registered.
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
        group.MapGet("/api/topology", (HihooiClient client) => JsonBody(BuildTopology(client)));
        group.MapGet("/api/replication", (HihooiClient client) => JsonBody(BuildReplication(client)));
        group.MapGet("/api/consistency", (HihooiClient client) => JsonBody(BuildConsistency(client)));

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
                        object payload = client is null
                            ? new { available = false, reason = "HihooiClient Not Available" }
                            : BuildMetrics(client);
                        await hub.Clients.All.SendAsync("metrics", payload, ct).ConfigureAwait(false);
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
            journal = new
            {
                configured = journal.Configured,
                available = journal.Available,
                note = journal.Note
            },
            extensions = LiveExtensions(snap),
            readsByExtension = snap.ReadsByExtension,
            routeReasons = snap.RouteReasons,
            errors = SanitizeErrors(snap.RecentErrors)
        };
    }

    private static object BuildTopology(HihooiClient client)
    {
        var snap = client.GetMetricsSnapshot();
        var nodes = new List<object>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var configured in client.Options.Extensions)
        {
            if (string.IsNullOrWhiteSpace(configured.Id) || !seen.Add(configured.Id))
                continue;
            var live = snap.Extensions.FirstOrDefault(e =>
                string.Equals(e.Id, configured.Id, StringComparison.OrdinalIgnoreCase));
            nodes.Add(live is null ? UnreportedExtension(configured.Id) : ReportedExtension(live));
        }

        foreach (var live in snap.Extensions)
        {
            if (!seen.Add(live.Id))
                continue;
            nodes.Add(ReportedExtension(live));
        }

        return new
        {
            available = true,
            latestTsId = client.LatestCommittedTsId,
            consistencyMode = client.Options.ConsistencyMode.ToString(),
            primary = new
            {
                id = "primary",
                role = "Primary",
                latestTsId = client.LatestCommittedTsId,
                primaryReads = snap.PrimaryReads
            },
            extensions = nodes
        };
    }

    private static object BuildReplication(HihooiClient client)
    {
        var snap = client.GetMetricsSnapshot();
        return new
        {
            available = true,
            latestTsId = client.LatestCommittedTsId,
            extensions = LiveExtensions(snap)
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

    private static object[] LiveExtensions(HihooiMetricsSnapshot snap) =>
        snap.Extensions
            .OrderBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
            .Select(ReportedExtension)
            .ToArray();

    private static object ReportedExtension(ExtensionMetrics e) => new
    {
        id = e.Id,
        reported = true,
        isOnline = (bool?)e.IsOnline,
        appliedTsId = (long?)e.AppliedTsId,
        lag = (long?)e.Lag,
        inflightReads = (int?)e.InflightReads,
        applyFailures = (long?)e.ApplyFailures
    };

    private static object UnreportedExtension(string id) => new
    {
        id,
        reported = false,
        isOnline = (bool?)null,
        appliedTsId = (long?)null,
        lag = (long?)null,
        inflightReads = (int?)null,
        applyFailures = (long?)null
    };

    private static (bool Configured, bool Available, string Note) JournalStatus(HihooiOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.RecoveryJournalDirectory))
        {
            return (false, false, "No recovery journal folder is configured.");
        }

        bool available;
        try
        {
            available = Directory.Exists(options.RecoveryJournalDirectory);
        }
        catch
        {
            available = false;
        }

        return available
            ? (true, true, "Journal folder is configured and reachable from this process.")
            : (true, false, "A journal folder is configured, but this process cannot see it on disk.");
    }

    /// <summary>Strip likely secrets from error lines before exposing to dashboard clients.</summary>
    internal static IReadOnlyList<string> SanitizeErrors(IReadOnlyList<string> errors)
    {
        static string Redact(string line)
        {
            // crude redaction of connection-string-like fragments and password= patterns
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
