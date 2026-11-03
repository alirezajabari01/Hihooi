# Consistency Modes

Paper §5.3. Session-granularity modes in `ConsistencyLevel`.

| Mode | Writes → Extensions | Reads | Guarantees (when engine SI enabled) |
|---|---|---|---|
| **Gssi** (default) | Async + Alg.2 | Alg.4 / Primary fallback | Aims for Global Strong SI (Theorem 1 assumptions) |
| **WeakSi** | Async | Any Extension | May read stale data |
| **RsiPc** | Async | Fully caught-up Extension; wait up to `RsiPcWaitTimeout` then **Primary fallback** | Adaptation: paper waits indefinitely |
| **OneCopySerializability** | Sync wait until all online Extensions apply | Any Extension | Fails if any Extension offline |

## SQL Server caveat
GSSI claim requires `ALLOW_SNAPSHOT_ISOLATION ON` and Snapshot transactions. See `Limitations.md`.
