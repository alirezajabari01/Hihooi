# SQL Server Limitations (vs paper assumptions)

Paper evaluation used **PostgreSQL 9.5** with Strong SI. This reimplementation targets SQL Server.

## Snapshot Isolation

- Theorem 1 (GSSI) assumes each replica engine guarantees **Strong SI**.
- SQL Server: enable `ALLOW_SNAPSHOT_ISOLATION ON`. Provider also tries `READ_COMMITTED_SNAPSHOT ON`.
- Sessions use `IsolationLevel.Snapshot` for transactions.
- If SI cannot be enabled (permissions), **do not claim GSSI**; configure databases manually before start.

## Commit ordering / TSID

- Paper: order by engine commit timestamps.
- Adaptation: before commit/rollback, TM reads `SYSUTCDATETIME()` on the Primary session and `TsIdAssigner` sorts concurrent completions by that timestamp.
- This is an engineering adaptation, not an identical PostgreSQL commit-timestamp API.

## Transactions Buffer

- Paper: Memcached.
- Library default: in-process `InMemoryTransactionsBuffer` (ordered log + per-Extension channels). Suitable for single-process hosting; replace via `ITransactionsBuffer` for distributed HA.

## Non-determinism (Table IV analogs)

| Issue | Handling |
|---|---|
| `SYSDATETIME` / `GETDATE` | Rewritten to a fixed UTC literal at analyze time |
| `RAND()` | Rewritten to `0` (coarse); prefer app-side constants |
| IDENTITY / sequences | Failed/rollback TStates still replayed then rolled back on Extensions |
| Non-deterministic procs/triggers | **Unsupported** (same as paper) |

## 1SR mode

- Implemented as: after Primary commit, wait until all online Extensions report contiguous applied TSID ≥ that write.
- Fine-grained failure semantics during sync wait are simplified vs a full distributed commit protocol (paper gives one sentence).

## RSI-PC wait

- Paper: wait until a fully consistent Extension exists.
- Adaptation: wait up to `RsiPcWaitTimeout`, then fall back to Primary to avoid unbounded blocking.

## Not implemented as full paper management plane

- HConsole UI, automated Seed DB backup orchestration via SQL Server BACKUP/RESTORE helpers, TM hot-standby process failover — scaffolded concepts (`ArchiverService`) but not a full ops product.
