using Hihooi;
using Hihooi.AspNetCore;
using Hihooi.Core.Consistency;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHihooi(o =>
{
    o.UseSqlServer();
    o.SetPrimary(
        builder.Configuration["Hihooi:Primary"]
        ?? Environment.GetEnvironmentVariable("HIHOOI_SQLSERVER_PRIMARY")
        ?? "Server=localhost;Database=HihooiPrimary;Trusted_Connection=True;TrustServerCertificate=True");

    var ext = builder.Configuration["Hihooi:Ext1"] ?? Environment.GetEnvironmentVariable("HIHOOI_SQLSERVER_EXT1");
    if (!string.IsNullOrWhiteSpace(ext))
        o.AddReplica(ext, "ext1");

    o.UseConsistency(ConsistencyLevel.Gssi);
    o.EnsureSnapshotIsolationOnStart = false;
    o.EnableDashboard = true;
    o.DashboardPath = "/hihooi";
    o.DashboardAuth.RequireAuthorization = true;
    o.DashboardAuth.ApiKey = builder.Configuration["Hihooi:DashboardApiKey"] ?? "dev-hihooi-key";
});

builder.Services.AddHihooiDashboard();

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", (IHihooiDatabase db) => Results.Content(
    """
    <html><body style="font-family:sans-serif">
    <h1>Hihooi WebHost Sample</h1>
    <p>Inject <code>IHihooiDatabase</code> — Client/Session stay internal to the engine.</p>
    <p><a href="/hihooi/">Open dashboard</a> (header <code>X-Hihooi-Dashboard-Key: dev-hihooi-key</code>).</p>
    <script>sessionStorage.setItem('hihooi-dashboard-key','dev-hihooi-key');</script>
    </body></html>
    """, "text/html"));

app.MapPost("/demo/insert", async (IHihooiDatabase db) =>
{
    var id = Random.Shared.Next(1000, 9999);
    await db.ExecuteAsync(
        """
        IF OBJECT_ID('dbo.HihooiDemo') IS NULL
          CREATE TABLE dbo.HihooiDemo (Id INT PRIMARY KEY, Name NVARCHAR(50));
        INSERT INTO dbo.HihooiDemo (Id, Name) VALUES (@id, @name);
        """,
        new { id, name = "demo-" + id });
    return Results.Ok(new { id });
});

app.MapHihooiDashboard();
app.Run();
