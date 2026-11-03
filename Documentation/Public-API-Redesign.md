# Public API Redesign Report — Hihooi 0.4.0

## 1. Previous architecture and problems

**Assemblies:** `Hihooi` (packable façade), `Hihooi.Core` (engine), `Hihooi.SqlServer`, `Hihooi.Redis`, `Hihooi.AspNetCore` (dashboard), all bundled into one NuGet.

**Consumer path (0.3.x):** resolve `HihooiClient` → call `StartAsync` → `OpenSession` → `ExecuteNonQueryAsync` / `QueryAsync` with positional `@p0` parameters → dispose session.

**Problems:**
- Application code had to understand Client/Session lifetime.
- Parameters were positional lists, not named objects.
- No typed row mapping (`IReadOnlyDictionary` only).
- SQL Server provider was hard-wired in DI with no fluent provider selection surface.
- Easy to bypass the intended public experience while still needing engine knowledge.

## 2. New architecture and rationale

A thin public façade **`IHihooiDatabase` / `HihooiDatabase`** sits on top of `HihooiTransactionManager`.

- Each public call opens a **short-lived** `HihooiSession`, runs one statement (auto-commit for writes), then disposes — safe under concurrent DI scopes.
- All calls still go through analyzer → router → primary/replica → buffer/replication → metrics/events.
- Named parameters via `SqlParameterBinder` (anonymous objects / dictionaries).
- Typed reads via `RowMapper` (no Dapper / no EF).
- Fluent options: `UseSqlServer()`, `SetPrimary()`, `AddReplica()`, `UseConsistency()`.
- `IHostedService` starts the engine at host startup; first-call lazy start remains as a safety net.
- `HihooiClient` remains for dashboard/ops (`EditorBrowsable.Advanced`); SQL helpers on it are obsolete.

## 3. Final `IHihooiDatabase`

```csharp
public interface IHihooiDatabase
{
    Task<int> ExecuteAsync(string sql, object? parameters = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default);
    Task<T?> QueryFirstOrDefaultAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default);
    Task<T> QuerySingleAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default);
}
```

- **ExecuteAsync** = writes only (analyzer must classify as write).
- **Query\*** = reads only, mapped to `T`.
- Transactions are intentionally **not** on the public API in 0.4.0.

## 4. DI registration sample

```csharp
services.AddHihooi(options =>
{
    options.UseSqlServer();
    options.SetPrimary(primaryConnectionString);
    options.AddReplica(replicaConnectionString); // optional, repeatable
    options.UseConsistency(ConsistencyLevel.Gssi);
    // options.UseRedisBuffer(redisCs);
    // options.EnableDashboard = true; // ASP.NET + AddHihooiDashboard
});
```

Registers: options, metrics, events, analyzer, buffer, SQL Server provider, TM, `HihooiClient` (dashboard), `IHihooiDatabase`, hosted starter.

## 5. Service usage sample

```csharp
public sealed class InventoryService(IHihooiDatabase database)
{
    public Task RestockAsync(int productId, int amount, CancellationToken ct = default) =>
        database.ExecuteAsync(
            "UPDATE Products SET Stock = Stock + @amount WHERE Id = @id",
            new { amount, id = productId },
            ct);

    public Task<Product?> GetAsync(int id, CancellationToken ct = default) =>
        database.QueryFirstOrDefaultAsync<Product>(
            "SELECT Id, Name, Stock, IsActive FROM Products WHERE Id = @id",
            new { id },
            ct);
}
```

## 6. Project / internal changes

| Area | Change |
|------|--------|
| `src/Hihooi` | `IHihooiDatabase`, `HihooiDatabase`, fluent options, hosted service, DI rewrite |
| `Hihooi.Core` | Named `Parameters` dictionaries; `SqlParameterBinder`; `RowMapper`; `ProviderKind` |
| `Hihooi.SqlServer` | Named SqlParameter binding |
| `Hihooi.Redis` / recovery journal | Parameter DTOs are dictionaries |
| Samples / README | Consumer path uses `IHihooiDatabase` only |
| Version | **0.4.0** (public API redesign) |

## 7. Tests executed (real results)

```
dotnet test tests/Hihooi.Tests -c Release
Passed!  Failed: 0, Passed: 53, Skipped: 0
```

Public API coverage (`tests/Hihooi.Tests/PublicApi/HihooiDatabaseApiTests.cs`): DI resolve, INSERT/UPDATE/DELETE/SELECT, POCO mapping, named params, concurrency, replication to replica store, offline→primary routing metrics, events/metrics on commit, fluent validation, binder/mapper units.

External consumer: `E:\Hihooi-external-nuget-test` restored **Hihooi 0.4.0** from `artifacts/` and built successfully against `IHihooiDatabase`.

Pack: `artifacts/Hihooi.0.4.0.nupkg`.

## 8. Remaining limitations

- Only **SQL Server** provider is implemented; `UsePostgreSQL` / `UseMySQL` throw `NotSupportedException`.
- No public **transaction** API yet.
- Typed mapping is reflection-based (no custom type handlers).
- Multi-batch SQL in one `ExecuteAsync` string is provider/SQL-dependent; prefer one statement per call.
- Live SQL Server / Redis still require environment (existing integration soft-path).
- `HihooiClient` / Core types remain visible in bundled DLLs but are not the recommended surface.

## 9. Compatibility with 0.3.1

| Item | Status |
|------|--------|
| Package id `Hihooi` | Same |
| Engine: routing, consistency modes, replication, recovery, Redis, dashboard auth | Preserved |
| Consumer SQL API | **Breaking** — prefer `IHihooiDatabase`; Client execute helpers obsolete |
| Parameter style | **Breaking** — named dictionaries (positional lists still bind as `@p0`…) |
| Version | **0.4.0** (not a patch on 0.3.1) |

## 10. NuGet publish readiness

**Ready to publish 0.4.0** as the public-API redesign release, with Known Limitations above and changelog noting the break from 0.3.1 Client/Session usage.

Do **not** present 0.3.1 as the recommended consumer API after this redesign.
