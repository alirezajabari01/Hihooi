using Hihooi;
using Hihooi.Core.Consistency;
using Microsoft.Extensions.DependencyInjection;

// Sample consumer — inject IHihooiDatabase only (no Client/Session).
Console.WriteLine("Hihooi Sample");

var primary = Environment.GetEnvironmentVariable("HIHOOI_SQLSERVER_PRIMARY");
var ext1 = Environment.GetEnvironmentVariable("HIHOOI_SQLSERVER_EXT1");

if (string.IsNullOrWhiteSpace(primary))
{
    Console.WriteLine("Set HIHOOI_SQLSERVER_PRIMARY (and optional HIHOOI_SQLSERVER_EXT1) to run a live sample.");
    return;
}

var services = new ServiceCollection();
services.AddHihooi(o =>
{
    o.UseSqlServer();
    o.SetPrimary(primary);
    if (!string.IsNullOrWhiteSpace(ext1))
        o.AddReplica(ext1, "ext1");
    o.UseConsistency(ConsistencyLevel.Gssi);
    o.EnsureSnapshotIsolationOnStart = true;
    o.RecoveryJournalDirectory = Path.Combine(Path.GetTempPath(), "hihooi-sample-journal");
});

await using var sp = services.BuildServiceProvider();
var db = sp.GetRequiredService<IHihooiDatabase>();

try
{
    await db.ExecuteAsync("""
        IF OBJECT_ID('dbo.HihooiSampleItems') IS NULL
        CREATE TABLE dbo.HihooiSampleItems (Id INT PRIMARY KEY, Name NVARCHAR(100) NOT NULL);
        """);

    await db.ExecuteAsync("DELETE FROM dbo.HihooiSampleItems WHERE Id = @id", new { id = 1 });
    await db.ExecuteAsync(
        "INSERT INTO dbo.HihooiSampleItems (Id, Name) VALUES (@id, @name)",
        new { id = 1, name = "hello" });

    var items = await db.QueryAsync<SampleItem>(
        "SELECT Id, Name FROM dbo.HihooiSampleItems WHERE Id = @id",
        new { id = 1 });

    Console.WriteLine($"Rows: {items.Count}; first={items.FirstOrDefault()?.Name}");
}
catch (Exception ex)
{
    Console.WriteLine("Sample failed: " + ex.Message);
}

sealed class SampleItem
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}
