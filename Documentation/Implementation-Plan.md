# Implementation Plan (C# + SQL Server)

**Constraint:** No code in this phase. Plan maps paper components → .NET/SQL Server **adaptations**; Hihooi algorithms stay as specified in `Algorithms.md` / `Replication.md`.

## 1. Software components (proposed)

| Paper | Proposed .NET artifact | Responsibility |
|---|---|---|
| JDBC/ODBC Driver | `Hihooi.Client` (ADO.NET-style API or proxy) | Session, SQL submit, result stream |
| Transaction Manager | `Hihooi.TransactionManager` (Windows Service / ASP.NET host) | Parse, route, TState, indexes, TSID |
| Transactions Buffer | `Hihooi.Buffer` (see Open-Questions for tech) | Ordered durable/HA queue of TStates |
| Extractor | `Hihooi.Extractor` (service per Extension host) | Alg. 2 apply + TSID reporting |
| Delivery Agent | Hosted inside Extension service or TM sidecar | Execute reads, stream results |
| Archiver | `Hihooi.Archiver` | Seed backups, buffer spill/prune |
| HController / HConsole | `Hihooi.Control` + CLI | Add/remove replicas, checkpoints, config |

**DB engines:** SQL Server instances as Primary, Seed, Extension (full copies). Snapshot Isolation / RCSI must be configured so Strong-SI assumptions of Theorem 1 hold (see Open-Questions).

## 2. Component interactions
```
App → Hihooi.Client → TM ─┬─ writes → Primary SQL Server
                          ├─ reads  → Delivery Agent @ Extension (or Primary)
                          └─ TState → Buffer → Extractors @ Extensions
Archiver ← applied-everywhere TStates; Seed ← backup(Primary @ TSID)
HController → start/stop services, register Extension endpoints
```

## 3. Algorithms to implement (paper-faithful)
1. SQL lightweight parser → table/column/row R/W sets + RAS/CAS/TAS (T-SQL dialect).
2. Alg. 1 independence; Alg. 2 Extractor scheduler; Alg. 3–4 TM indexes + routing.
3. Contiguous TSID reporting; least-loaded selection among eligible replicas.
4. Statement-level LB for multi-statement writes under GSSI.
5. Session modes: GSSI (default), Weak SI, RSI-PC, 1SR.
6. Query rewrite for non-determinism (Table IV analogs for T-SQL).

## 4. Database structure (middleware + data)
**Application data:** fully replicated user DB on Primary/Seed/Extensions — schema owned by app; Hihooi does not redesign it.

**Middleware metadata (suggested; not in paper schema):**
- Cluster membership (roles, endpoints, applied TSID).
- Seed checkpoint record: `{seedId, associatedTsId, createdAt, storagePath}`.
- Optional local Extractor apply log (for re-add resume) — paper mentions logging completed applies [p. 10].

## 5. Transaction management
- TM owns client sessions; maps ADO.NET-like begin/commit/rollback to Primary.
- Capture SQL Server commit ordering for TSID (mechanism TBD — Open-Questions).
- Retain TStates until all Extensions ack contiguous apply.

## 6. Replication mechanism
- Statement replay via parameterized batches on Extension connections.
- Parallelism only via Alg. 2; 1SR mode = wait-for-all-apply before commit ack to client (paper: sync execute on all Extensions).
- Seed: SQL Server backup/restore or AG-style copy **as utility**, always tagged with TSID (adaptation of §6.1).

## 7. Error handling & recovery
Implement paper matrix in `Replication.md` (Primary / TM / Buffer / Archiver / Extension). Prefer Seed promotion path for Primary loss.

## 8. Tests required
| Area | Tests |
|---|---|
| Unit | Alg. 1–4 on synthetic R/W sets; refcount merge/remove; contiguous TSID reporter |
| Parser | T-SQL fixtures → expected sets/classes (incl. DDL→TAS, PK equality RAS) |
| Integration | Write on Primary → appear on Extensions in allowed parallel orders; lag + Alg. 4 routing |
| Consistency | GSSI scenarios from Theorem 1 sketch; session modes Weak/RSI-PC/1SR |
| Non-determinism | IDENTITY/sequences, `SYSDATETIME`, RAND-like (rewrite/replay rules) |
| Failure | Kill Buffer/TM/Extension; verify paper recovery behaviors |
| Elasticity | Add Extension from Seed+Archiver Buffer without global barrier |
| Load | TPC-C / YCSB-style mixes (paper §VII) as stretch goals |

## 9. Suggested build order
1. TState model + Alg. 1–2 in-process (no network).
2. TM stub + Primary execute + TSID + Buffer + single Extractor serial then parallel.
3. Indexes Alg. 3–4 + read routing + Delivery Agent.
4. Multi-statement statement-level LB + consistency modes.
5. Parser + Table IV rewrites for T-SQL.
6. Archiver + Seed create/add/remove Extension.
7. Failure recovery paths + HA Buffer/TM backup.
8. Client library + Control CLI; benchmarks.
