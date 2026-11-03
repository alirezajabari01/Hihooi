using Hihooi;
using Hihooi.Core.Configuration;
using Hihooi.Core.Consistency;
using Hihooi.Dashboard.Hubs;
using Microsoft.AspNetCore.SignalR;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSignalR();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyHeader().AllowAnyMethod().AllowCredentials().SetIsOriginAllowed(_ => true)));

var primary = builder.Configuration["Hihooi:Primary"] ?? Environment.GetEnvironmentVariable("HIHOOI_SQLSERVER_PRIMARY");
var ext1 = builder.Configuration["Hihooi:Ext1"] ?? Environment.GetEnvironmentVariable("HIHOOI_SQLSERVER_EXT1");
var hasSql = !string.IsNullOrWhiteSpace(primary);

if (hasSql)
{
    builder.Services.AddHihooi(o =>
    {
        o.PrimaryConnectionString = primary!;
        if (!string.IsNullOrWhiteSpace(ext1))
            o.Extensions.Add(new ExtensionEndpoint { Id = "ext1", ConnectionString = ext1! });
        o.ConsistencyMode = ConsistencyLevel.Gssi;
        o.EnsureSnapshotIsolationOnStart = false; // operator must pre-enable SI in shared envs
        o.RecoveryJournalDirectory = Path.Combine(builder.Environment.ContentRootPath, "journal");
    });
}

builder.Services.AddHostedService<MetricsBroadcastService>();

var app = builder.Build();
app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapHub<MetricsHub>("/hubs/metrics");

app.MapGet("/api/health", () => Results.Ok(new
{
    status = "ok",
    sqlConfigured = hasSql,
    mode = hasSql ? "live" : "metrics-standalone"
}));

app.MapGet("/api/metrics", (IServiceProvider sp) =>
{
    if (!hasSql) return Results.Ok(new { available = false, reason = "SQL Server not configured (set Hihooi:Primary or HIHOOI_SQLSERVER_PRIMARY)" });
    var client = sp.GetRequiredService<HihooiClient>();
    var snap = client.GetMetricsSnapshot();
    return Results.Ok(new
    {
        available = true,
        snap.StartedUtc,
        snap.Commits,
        snap.Rollbacks,
        snap.FailedApplies,
        snap.PrimaryReads,
        snap.ExtensionReads,
        snap.PrimaryFallbacks,
        snap.ConflictsDetected,
        snap.ParallelAppliesStarted,
        latestTsId = client.LatestCommittedTsId,
        extensions = snap.Extensions.Select(e => new
        {
            e.Id,
            e.IsOnline,
            e.AppliedTsId,
            e.Lag,
            e.InflightReads,
            e.ApplyFailures
        }),
        snap.ReadsByExtension,
        snap.RouteReasons,
        errors = snap.RecentErrors
    });
});

if (hasSql)
{
    var client = app.Services.GetRequiredService<HihooiClient>();
    await client.StartAsync();
}

app.Run();

public sealed class MetricsBroadcastService : BackgroundService
{
    private readonly IHubContext<MetricsHub> _hub;
    private readonly IServiceProvider _sp;

    public MetricsBroadcastService(IHubContext<MetricsHub> hub, IServiceProvider sp)
    {
        _hub = hub;
        _sp = sp;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var client = _sp.GetService<HihooiClient>();
                object payload;
                if (client is null)
                {
                    payload = new { available = false, reason = "Not Available — SQL Server not configured" };
                }
                else
                {
                    var snap = client.GetMetricsSnapshot();
                    payload = new
                    {
                        available = true,
                        snap.Commits,
                        snap.Rollbacks,
                        snap.FailedApplies,
                        snap.PrimaryReads,
                        snap.ExtensionReads,
                        snap.PrimaryFallbacks,
                        snap.ConflictsDetected,
                        snap.ParallelAppliesStarted,
                        latestTsId = client.LatestCommittedTsId,
                        extensions = snap.Extensions,
                        snap.RouteReasons,
                        errors = snap.RecentErrors
                    };
                }
                await _hub.Clients.All.SendAsync("metrics", payload, stoppingToken).ConfigureAwait(false);
            }
            catch
            {
                await _hub.Clients.All.SendAsync("metrics", new { available = false, reason = "Not Available" }, stoppingToken).ConfigureAwait(false);
            }
            await Task.Delay(1000, stoppingToken).ConfigureAwait(false);
        }
    }
}
