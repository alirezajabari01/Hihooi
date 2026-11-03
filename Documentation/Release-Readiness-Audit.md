# Release Readiness Audit — Hihooi

| Field | Value |
|-------|--------|
| Audit date | 2026-11-03 |
| Audited package version | **0.3.1** |
| Requested baseline | 0.3.0 (not published as-is; see Version Policy) |
| Verdict | **Ready with Known Limitations** |

## Verdict

**Ready with Known Limitations** — the single NuGet package `Hihooi` builds, packs, installs outside the solution, and passes the in-repo Release test suite after security, recovery, and Redis buffer hardening. Remaining gaps are environmental (live SQL Server / Redis), architectural (no automatic primary failover, no multi-TM fencing), and documentation/scope (paper vs reimplementation, `net8.0` only).

Not **Ready for Public Release** (unqualified) because live multi-node consistency and Redis/SQL Server integration are not continuously proven in CI without external services.

Not **Not Ready** because blockers for a first public MIT NuGet (auth fail-closed dashboard, license metadata, pack/install, recovery dedupe, Release build/tests) were addressed in 0.3.1.

---

## Version Policy

| Version | Status |
|---------|--------|
| **0.3.0** | Prior artifact; **do not publish** after this audit. Missing FixedTimeEquals API-key compare, applied-TSID watermark store, Redis TSID-keyed idempotent storage, and MIT pack metadata completeness. |
| **0.3.1** | **Recommended publish version.** Patch-level: security, recovery, Redis buffer correctness, LICENSE/README packaging. No intentional public API break. |
| **0.4.0** | Suggest only if later work adds multi-TFM, automatic failover, or multi-TM fencing. |

---

## Checklist Results

### 1. Dashboard endpoint security / unauthorized access

| Result | **Pass (with limitation)** |
|--------|----------------------------|
| Evidence | `MapHihooiDashboard` maps all routes under `RequireAuthorization(DashboardAuthConstants.Policy)`. `DashboardAuthHandler` fails closed when `RequireAuthorization` is true unless API key or a host-authenticated user is present. Scheme is **not** set as the host default. Regression: `tests/Hihooi.Tests/Security/DashboardAuthTests.cs`. |
| Fixes | Policy on group; host-auth path; fail message without leaking secrets. |
| Limitation | `RequireAuthorization=false` intentionally opens the dashboard (operator choice). No end-to-end HTTP 401 test via `WebApplicationFactory` in this suite. |

### 2. API Key authentication / sensitive data leakage

| Result | **Pass** |
|--------|----------|
| Evidence | API key compared with `CryptographicOperations.FixedTimeEquals` after UTF-8 length check (`DashboardAuthHandler.ApiKeyMatches`). Metrics JSON omits connection strings; `SanitizeErrors` redacts `Password=` / `ApiKey=` style fragments. Tests cover match/mismatch and redaction. |
| Fixes | Fixed-time compare; error sanitization on `/api/metrics` and SignalR payload path. |

### 3. NuGet install in independent project outside Solution

| Result | **Pass** |
|--------|----------|
| Evidence | Packed `artifacts/Hihooi.0.3.1.nupkg`. Created `E:\Hihooi-external-nuget-test` (outside solution), `dotnet add package Hihooi --version 0.3.1 --source …\artifacts`, compiled against `Hihooi`, `Hihooi.Core.Configuration`, `Hihooi.Core.Consistency`. `samples/Hihooi.WebHost` also restores/builds with PackageReference `0.3.1` only (kept out of `Hihooi.sln` to avoid restore coupling). |
| Limitation | In-repo `Hihooi.Sample` uses ProjectReferences to Core/SqlServer because `PrivateAssets=all` does not flow compile assets—package consumers do not need that workaround. |

### 4. NuGet dependencies and Target Framework

| Result | **Pass (with limitation)** |
|--------|----------------------------|
| Evidence | `TargetFramework`: `net8.0` only. Nuspec dependencies: `Microsoft.Data.SqlClient` 5.2.2, `Microsoft.Extensions.DependencyInjection.Abstractions` 8.0.2, `StackExchange.Redis` 2.8.16. Bundled: `Hihooi.Core`, `Hihooi.SqlServer`, `Hihooi.Redis`, `Hihooi.AspNetCore`. Release build of solution succeeded (0 warnings). |
| Limitation | No `netstandard2.0` / `net6.0` / `net9.0` multi-targeting. AspNetCore dashboard requires `Microsoft.AspNetCore.App` at runtime when EnableDashboard is used. |

### 5. TSID and commit ordering under concurrency

| Result | **Pass** |
|--------|----------|
| Evidence | `ConcurrentTsIdTests.Many_parallel_commits_produce_unique_monotonic_tsids` — 50 parallel commits → unique TSIDs 1..50. Additional unit coverage: `TsIdAssignerTests`, `ContiguousTsIdTrackerTests`. |
| Limitation | Ordering proven for single TM process; no multi-TM fencing (documented non-goal). |

### 6. Four Consistency Modes in realistic scenarios

| Result | **Pass (with limitation)** |
|--------|----------------------------|
| Evidence | `ConsistencyModeRoutingTests`: GSSI lag→primary; GSSI caught-up→extension; Weak SI→extension while lagging; RSI-PC timeout→primary; 1SR→extension. Offline routing: `ReplicaOfflineBehaviorTests`, `OneCopySerializabilityOfflineTests`. |
| Limitation | Router/fake-provider level, not a live four-mode SQL Server matrix in CI. Paper-level guarantees depend on SI configuration and replica health. |

### 7. Prevention of re-applying transactions in Recovery

| Result | **Pass** |
|--------|----------|
| Evidence | `FileAppliedTsIdStore` persists per-extension watermark; Extractor loads watermark, resets contiguous tracker, skips `TsId <= ContiguousApplied` and exact-set members; saves watermark after apply. Regression: `AppliedTsIdStoreAndDedupeTests`. Journal republish path still present for crash recovery without double-apply when watermark exists. |
| Limitation | Crash between apply and watermark save can still leave a window; SQL Server apply must remain idempotent for safety. |

### 8. Behavior when Primary / Replica disconnect

| Result | **Pass (with limitation)** |
|--------|----------------------------|
| Evidence | Marking extension offline routes GSSI reads to primary (`ReplicaOfflineBehaviorTests`). Extractor marks extension offline on apply failure. 1SR offline behavior covered by dedicated tests. |
| Limitation | **No automatic primary election / failover.** Primary outage surfaces as client errors; operator must restore or reconfigure. |

### 9. Redis Buffer error paths

| Result | **Pass (with limitation)** |
|--------|----------------------------|
| Evidence | Buffer stores ZSET members by TSID + HASH payloads (idempotent overwrite). Publish/ack wrap `RedisException` as `InvalidOperationException` with TSID context. Subscribe loop backs off on Redis errors without tearing down the process blindly. Regression: `RedisBufferGuardTests` (null mux). |
| Limitation | No live Redis server integration test in CI. No multi-writer fencing. |

### 10. README and install documentation

| Result | **Pass** |
|--------|----------|
| Evidence | Root `README.md` documents pack/install, library usage, dashboard auth (API key + middleware order), `net8.0`, dependency note, and pointer to this audit. Packaged as `PackageReadmeFile`. |
| Limitation | `PackageProjectUrl` is a placeholder (`https://github.com/hihooi`); update before marketing the package if the real repo URL differs. |

### 11. Full Release build and runnable tests

| Result | **Pass (with limitation)** |
|--------|----------------------------|
| Evidence | `dotnet build Hihooi.sln -c Release` — succeeded, 0 warnings. `dotnet test tests/Hihooi.Tests -c Release` — **43 passed, 0 failed, 0 skipped**. `dotnet pack` produced `Hihooi.0.3.1.nupkg`. No tests removed or disabled for this audit. |
| Limitation | `SqlServerIntegrationTests` no-ops (early return) when `HIHOOI_SQLSERVER_*` env vars are unset—test remains enabled but does not exercise SQL Server in default CI. |

### 12. Dependency licenses and project License for public release

| Result | **Pass** |
|--------|----------|
| Evidence | Project `LICENSE` = MIT; `PackageLicenseExpression=MIT`; LICENSE packed into nupkg. Declared deps are MIT-family (`Microsoft.Data.SqlClient`, `StackExchange.Redis`, Microsoft.Extensions.*). |
| Limitation | Third-party license texts are not vendored inside the nupkg beyond NuGet dependency metadata—standard for NuGet, but legal review may still want a NOTICE file for enterprise consumers. |

---

## Regression tests added / exercised in this audit

| Area | Test |
|------|------|
| Dashboard auth / redaction | `Security/DashboardAuthTests.cs` |
| Recovery watermark / dedupe | `Recovery/AppliedTsIdStoreAndDedupeTests.cs` |
| Consistency modes | `Consistency/ConsistencyModeRoutingTests.cs` |
| Replica offline | `Integration/ReplicaOfflineBehaviorTests.cs` |
| Redis ctor guard | `Redis/RedisBufferGuardTests.cs` |
| Concurrent TSID | `Concurrency/ConcurrentTsIdTests.cs` (existing, re-run) |

---

## Known Limitations (summary)

1. Single-process Transaction Manager; no distributed TM fencing.
2. No automatic primary failover.
3. Live SQL Server / Redis paths not asserted without external services.
4. `net8.0` only.
5. Consistency modes validated primarily via router/fake provider, not paper-grade distributed experiments.
6. Dashboard can be opened by setting `RequireAuthorization=false`.
7. Placeholder package project URL.

---

## Publish recommendation

Publish **`Hihooi` 0.3.1** to nuget.org (or private feed) with MIT license, after replacing `PackageProjectUrl` / repository metadata with the real public repository URL if available. Document Known Limitations in release notes. Keep **0.3.0** unpublished or unlist if already pushed.
