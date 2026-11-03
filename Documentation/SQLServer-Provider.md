# SQL Server Provider

Project: `Hihooi.SqlServer`

- `SqlServerDatabaseProvider` / `SqlServerDatabaseSession`
- Connections via `Microsoft.Data.SqlClient` (pooling by default)
- Transactions: `IsolationLevel.Snapshot`
- Parameters: positional `@p0`, `@p1`, …
- Command timeout: `HihooiOptions.CommandTimeoutSeconds`
- SI bootstrap: `EnsureSnapshotIsolationAsync` (throws if cannot enable/verify)
- Errors wrapped with replica id; connections disposed on session dispose

## Unsupported / safe paths
Complex T-SQL analyzed conservatively as TAS (see analyzer). Stored procs, dynamic SQL, temp tables → safe write TAS fallback.
