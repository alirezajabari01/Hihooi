# Hihooi Dashboard Observability (v0.5.0)

## What changed

Hihooi now records **per-request distributed traces** on the real `IHihooiDatabase` execution path and exposes them through the embedded dashboard.

## Enable durable storage

```csharp
services.AddHihooi(o =>
{
    o.UseSqlServer();
    o.SetPrimary(primary);
    o.AddReplica(replica);
    o.EnableDashboard = true;
    o.Observability.Enabled = true;
    o.Observability.StorageDirectory = Path.Combine(Path.GetTempPath(), "hihooi-traces");
    o.Observability.MaskSensitiveParameters = true;
});
```

Without `StorageDirectory`, traces stay in a bounded in-memory ring (lost on process exit).

## Trace path (real engine)

1. `IHihooiDatabase` opens a root span (`api.entry`)
2. `HihooiSession` adds `classify.analyze`, `routing.decision` (with **candidate evaluations**), `connection.acquire`, `sql.execute`, `result.materialize`
3. Writes select **primary**; reads use `QueryRouter` + health eligibility
4. Replication applies emit separate `replication.apply` traces from `ExtractorService`
5. Completion is async via a bounded channel (never fails the SQL path)

## Dashboard APIs

Under `DashboardPath` (default `/hihooi`), authenticated:

- `/api/overview`, `/api/nodes`, `/api/nodes/{id}`, `/api/health`
- `/api/traffic`, `/api/traces`, `/api/traces/{traceId}`
- `/api/replication`, `/api/analytics/queries`
- `/api/consistency`, `/api/connections`, `/api/events`, `/api/config`

UI pages: Overview, Nodes, Live Traffic, Request Tracing (+ Trace Waterfall), Replication, Query Analytics, Consistency, Connections, Errors & Events, Health Checks, Configuration.

## Notes

- No OpenTelemetry NuGet dependency; `System.Diagnostics.Activity` is used for W3C correlation when `Activity.Current` exists.
- Analytics percentiles are computed **only** from stored traces.
- Health status comes from periodic `SELECT 1` probes, not from “recent traffic”.
