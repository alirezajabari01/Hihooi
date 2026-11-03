# Usage

## Configure Primary + Extensions

All nodes must host the **same application schema** (full replicas). Hihooi does not create app tables.

```csharp
var options = new HihooiOptions
{
    PrimaryConnectionString = primaryCs,
    Extensions =
    {
        new ExtensionEndpoint { Id = "ext1", ConnectionString = ext1Cs },
        new ExtensionEndpoint { Id = "ext2", ConnectionString = ext2Cs }
    },
    DefaultConsistencyLevel = ConsistencyLevel.Gssi,
    RsiPcWaitTimeout = TimeSpan.FromSeconds(5),
    EnsureSnapshotIsolationOnStart = true
};
```

## Public API surface

Consumers typically use only:

- `HihooiClient` — start cluster services, open sessions, add/mark Extensions
- `HihooiSession` — `BeginTransactionAsync`, `ExecuteNonQueryAsync`, `QueryAsync`, `CommitAsync`, `RollbackAsync`
- `ConsistencyLevel` — optional per-session override on `OpenSession`

Internal types (indexes, TSID assigner, Alg. 1–4) are not required for normal use.

## Write / read flow

1. Writes always execute on Primary; on commit/rollback a TState is published and applied on Extensions (Alg. 2).
2. Reads are routed per session consistency (Alg. 4 for GSSI); fallback is Primary.
3. Inside a multi-statement write, independent read statements may go to Extensions (statement-level LB).

## Parameter style

Use positional `@p0`, `@p1`, … matching the `parameters` list order (ADO.NET style used by the SQL Server provider).

## NuGet

```bash
dotnet pack Hihooi.sln -c Release -o artifacts
dotnet add package Hihooi --source ./artifacts
```
