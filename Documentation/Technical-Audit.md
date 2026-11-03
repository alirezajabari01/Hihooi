# Technical Audit — Hihooi .NET MVP

**Date:** 2026-11-03  
**Scope:** Code vs `Documentation/*` (no PDF re-analysis).  
**Policy:** No new features; only proven bug fixes applied (see §Fixes Applied).

## Summary verdict

Core Algorithms 1–4 are **structurally aligned** with the docs, but several **correctness / safety gaps** existed around index `*` handling, Extractor failure deadlock, 1SR offline skipping, INSERT classification, and silent SI setup failure. After fixes below, MVP is **algorithmically usable with known residual risks** (parser coarseness, in-memory buffer, TSID timestamp approximation). **Not** production-equivalent to the full paper system.

---

## 1. Algorithms 1–4 vs documentation

| Alg | Status | Notes |
|---|---|---|
| 1 `areIndependent` | Mostly correct | Cross table w/rw check + CAS/RAS column/row rules match docs. Added guard when table conflict yields empty common set. |
| 2 Parallel apply | Logic correct | Scheduler matches onNew/onComplete + waiting-state check. Extractor previously could deadlock on apply failure (**fixed**). |
| 3 `updateIndexes` | Correct for RAS/CAS/TAS paths | TAS/CAS → CIndex; RAS → RIndex; TIndex always on table writes. |
| 4 `findLatestConsistentTSID` | Had gap | Did not honor CIndex `"*"` from INSERT/TAS → under-routed reads (**fixed**). |

### Findings

| ID | Severity | Location | Issue | Impact | Fix |
|---|---|---|---|---|---|
| A1 | **High** | `ReplicaIndexes.FindLatestConsistentTsId` | Column lookup ignored `table.*` entries written by TAS/INSERT | GSSI could route reads to stale Extension after INSERT | Treat `*` as matching any column (`LookupColumns`) |
| A2 | **Medium** | `IndependenceAnalyzer.AreIndependent` | If coarse table conflict with empty common rw set, returned independent | Theoretically unsafe parallel apply | Return `false` when `common` empty after conflict |
| A3 | Low | Alg.1 statement-LB | RAS write vs CAS read always conflicts | Over-conservative (extra Primary load), not incorrect | Leave; safer than under-approx |

---

## 2. TSID & commit timestamp

| ID | Severity | Location | Issue | Impact | Fix / note |
|---|---|---|---|---|---|
| T1 | **Medium** | `HihooiTransactionManager.CompleteWriteTransactionAsync` | Timestamp via `SYSUTCDATETIME()` **before** Commit, not engine commit LSN/time | Ordering approximates paper commit-timestamp order; ties broken by `TransactionId` | Documented adaptation; keep unless SQL Server commit-order API added |
| T2 | **Medium** | `TsIdAssigner` | Flush only when `_inflightCommits==0` | One hung commit blocks all TSID assignment | Operational risk; consider timeout/lease in future (not done) |
| T3 | Low | `TsIdAssigner` | Contiguous assignment OK; out-of-order completion covered by unit test | — | OK |

Contiguous reporting (`ContiguousTsIdTracker`) matches docs (gap-free prefix).

---

## 3. Parallel Apply & races

| ID | Severity | Location | Issue | Impact | Fix |
|---|---|---|---|---|---|
| P1 | **High** | `ExtractorService.ApplyAndContinueAsync` | Apply exception left txn in `runningState`; fire-and-forget task | Permanent Alg.2 stall for that Extension | Always `OnTransactionComplete`; mark Extension offline; do not advance contiguous TSID on failure |
| P2 | Medium | `ExtractorService` | `_ = ApplyAndContinueAsync(...)` unobserved tasks | Harder failure diagnosis | Partial: errors logged; full Task tracking not added |
| P3 | Low | `ParallelApplyScheduler` | Lock protects state; apply runs outside lock | Correct design | OK |

---

## 4. TIndex / CIndex / RIndex

Update rules match Alg.3. Prune uses min applied across Extensions.

Residual: complex multi-table statements depend on analyzer quality (see §9).

---

## 5. findLatestConsistentTSID & routing

| ID | Severity | Location | Issue | Impact | Fix |
|---|---|---|---|---|---|
| R1 | **High** | Alg.4 + INSERT analyzer | See A1 + INSERT downgraded to CAS | Stale GSSI reads | Fixed INSERT→TAS+`*` and `*` lookup |
| R2 | Low | `QueryRouter` least-loaded | Metric = inflight read leases (adaptation) | Paper unspecified | OK as adaptation |
| R3 | Low | GSSI fallback to Primary | Matches docs | — | OK |

---

## 6. Consistency modes

| Mode | Implementation | Verdict |
|---|---|---|
| GSSI | Alg.4 + Primary fallback | OK after A1 fix; SI dependency remains |
| Weak SI | Any Extension | OK (allows stale by design) |
| RSI-PC | Wait for fully caught-up; timeout→Primary | **Deviation**: paper waits; we timeout (**documented adaptation**) |
| 1SR | Sync wait after publish | Was skipping offline Extensions (**fixed** → throw) |

| ID | Severity | Location | Issue | Fix |
|---|---|---|---|---|
| C1 | **High** | `WaitUntilAppliedOnAllAsync` | `!IsOnline \|\| applied` skipped offline | Require all online; else throw |
| C2 | Medium | `RouteFullyConsistent` | `Thread.Sleep` + Primary fallback | Documented; async wait preferred later |

---

## 7. Transaction / Commit / Rollback

| ID | Severity | Location | Issue | Impact | Fix |
|---|---|---|---|---|---|
| X1 | Medium | Rollback path | Rollback still gets TSID + published (paper: replicate failed txns) | Correct intent for sequences; indexes not updated on rollback | OK |
| X2 | Medium | `HihooiSession` | Not thread-safe (one session / one caller) | Data races if shared | Document; no API change |
| X3 | Low | Auto-commit `ExecuteNonQuery` | Begin+write+Commit | OK | OK |
| X4 | Low | Statement-level LB | Independent reads may hit Extension mid-write txn | Matches §5.2 under SI | OK |

---

## 8. SQL Server Snapshot Isolation vs paper GSSI

| ID | Severity | Location | Issue | Fix |
|---|---|---|---|---|
| S1 | **High** | `SqlServerDatabaseProvider.EnsureSnapshotIsolationAsync` | Swallowed `SqlException` → Start succeeded without SI | Now throws if alter fails or state ≠ ON |
| S2 | Medium | SQL Server vs PostgreSQL Strong SI | Semantics differ (update-conflict abort rules, etc.) | Keep `SQL-Server-Limitations.md`; do not claim identical proof |
| S3 | Low | Session uses `IsolationLevel.Snapshot` | Aligns with SI requirement when DB option ON | OK |

---

## 9. SQL Analyzer risks (R/W set errors)

| ID | Severity | Location | Risk |
|---|---|---|---|
| Q1 | **High** | Joins/subqueries/CTEs/MERGE/OUTPUT | Under/over-approx tables → wrong independence & routing |
| Q2 | **High** | PK detection | Any `col=literal` treated as row key (not verified PK/unique) → false RAS independence |
| Q3 | Medium | Aliases / multi-part names | Column attribution coarse |
| Q4 | Medium | Was: INSERT with column list → CAS | **Fixed** → always TAS + `*` |
| Q5 | Medium | `SYSDATETIME` rewrite at analyze time | Value frozen early; OK for replica equality, not “commit time” |
| Q6 | Low | `RAND()` → `0` | Coarse Table IV adaptation |

---

## 10. Thread safety

| Component | Assessment |
|---|---|
| `ReplicaIndexes`, `ExtensionRegistry`, `TsIdAssigner`, `ParallelApplyScheduler`, `InMemoryTransactionsBuffer`, `ContiguousTsIdTracker` | Locked / concurrent-safe for intended use |
| `HihooiSession` | **Not** concurrent |
| `ExtractorService` | Scheduler locked; apply parallel; failure path fixed |
| `QueryRouter` RSI-PC | Blocks thread (Sleep) |
| `HihooiTransactionManager._extractors` | `AddExtension` not synchronized with `DisposeAsync` (Low) |

---

## 11. Test quality

**Before audit:** 23 tests — good unit coverage for Alg.1 happy paths, Alg.2 examples, Alg.3/4 paper table sketch, TSID reorder, fake E2E smoke. **Weak** on: `*` index, apply failure, 1SR offline, analyzer adversarial SQL, true concurrent Extractor stress, live SI proof.

**Added in audit:**
- `ReplicaIndexesStarTests` — INSERT/TAS `*` blocks CAS read
- `ParallelApplyFailureTests` — complete releases waiters
- `OneCopySerializabilityOfflineTests` — 1SR fails if Extension offline

**Still missing (not fixed now):** adversarial SQL parser suite; Extractor integration with forced apply throw; multi-Extension race; RSI-PC wait semantics; SQL Server SI gate integration test.

---

## Fixes applied this audit

1. `ReplicaIndexes` — `LookupColumns` honors `table.*`
2. `LightweightSqlAnalyzer.ParseInsert` — always TAS + `*`
3. `ExtractorService.ApplyAndContinueAsync` — always free scheduler; offline on failure
4. `WaitUntilAppliedOnAllAsync` — 1SR does not skip offline
5. `IndependenceAnalyzer` — empty common after conflict → dependent
6. `SqlServerDatabaseProvider.EnsureSnapshotIsolationAsync` — verify + throw
7. Related unit/integration tests

---

## Residual open risks (no code change)

- Distributed buffer / TM HA still absent  
- Seed/backup automation incomplete  
- Parser not semantically complete  
- TSID uses pre-commit `SYSUTCDATETIME` approximation  
- RSI-PC timeout→Primary adaptation  

---

## Project status after audit

| Criterion | Status |
|---|---|
| Build / NuGet | OK |
| Core algorithms faithful enough for MVP | **Yes, with fixes** |
| Safe to claim full paper GSSI on SQL Server | **No** (SI + parser + timestamp caveats) |
| Ready for new features | **After review of this audit** |
