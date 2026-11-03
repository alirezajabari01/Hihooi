# Hihooi (.NET)

Single NuGet package **`Hihooi`** (v0.4.0): Core algorithms, SQL Server provider, Redis buffer, and embedded ASP.NET Core dashboard.

## Install
```bash
dotnet pack src/Hihooi/Hihooi.csproj -c Release -o artifacts
dotnet add package Hihooi --source ./artifacts
```

## Usage (recommended)

Inject **`IHihooiDatabase`** — do not open Client/Session in application code.

```csharp
services.AddHihooi(options =>
{
    options.UseSqlServer();
    options.SetPrimary(primaryConnectionString);
    options.AddReplica(replicaConnectionString);
    options.UseConsistency(ConsistencyLevel.Gssi);
});
```

```csharp
public sealed class ProductService(IHihooiDatabase db)
{
    public Task<int> RestockAsync(int id, int amount) =>
        db.ExecuteAsync(
            "UPDATE Products SET Stock = Stock + @amount WHERE Id = @id",
            new { amount, id });

    public Task<IReadOnlyList<Product>> ActiveAsync() =>
        db.QueryAsync<Product>(
            "SELECT Id, Name, Stock, IsActive FROM Products WHERE IsActive = @active",
            new { active = true });
}
```

Writes use `ExecuteAsync`. Reads use `QueryAsync` / `QueryFirstOrDefaultAsync` / `QuerySingleAsync`. Parameters are named (anonymous objects or dictionaries). Each call opens a short-lived engine session so connections are not shared across requests. Routing, consistency, replication, metrics, and events stay on the engine path.

## Dashboard (ASP.NET Core only)
```csharp
o.EnableDashboard = true;
o.DashboardPath = "/hihooi";
o.DashboardAuth.ApiKey = "secret";
builder.Services.AddHihooiDashboard();
app.UseAuthentication();
app.UseAuthorization();
app.MapHihooiDashboard();
```

Sample hosts: `samples/Hihooi.Sample`, `samples/Hihooi.WebHost` (PackageReference only).

**Target:** `net8.0`. Provider today: SQL Server only (`UsePostgreSQL` / `UseMySQL` throw `NotSupportedException` until implemented).

## Docs
See `Documentation/` (including `Public-API-Redesign.md`).
