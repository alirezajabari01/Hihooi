# Testing

```bash
dotnet test Hihooi.sln -c Release
```

## Coverage
- Unit: Alg.1–4, TSID contiguous/reorder, analyzer safe fallbacks, journal
- Fake integration: replication, rollback, consistency modes, offline 1SR
- Concurrency: 50 parallel commits → unique monotonic TSIDs
- SQL Server live: `SqlServerIntegrationTests` runs only if `HIHOOI_SQLSERVER_PRIMARY` + `HIHOOI_SQLSERVER_EXT1` set

## Redis
No live Redis in CI by default. Implementation under `Hihooi.Redis`; requires reachable Redis for integration.

## Latest local result
See `FINAL-DELIVERY-REPORT.md`.
