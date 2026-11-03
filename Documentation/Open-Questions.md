# Open Questions / Incomplete Information

Items below are **not specified** or only vaguely specified in `References/hihooi.pdf`. Do not invent Hihooi behavior for these until resolved (extra papers, authors, or explicit project decisions marked as *adaptation*).

## Source coverage
- Repo `References/` currently contains only the TKDE Hihooi paper PDF. Related works (Ganymed, demo SIGMOD’21, auto-scaling short paper) were **not** ingested as project sources yet.
- Internal source code / reference implementation of Hihooi is **not** available here.

## Ambiguities in the paper

### Client protocol & API
- Exact Hihooi JDBC/ODBC API surface, wire protocol between Driver ↔ TM ↔ Delivery Agent, and how incremental result streaming is framed: **not specified**. [§III]
- How clients discover TM failover / switch-over after TM recovery: high-level only. [§6.1]

### Parsing & R/W extraction
- Concrete grammar/parser implementation, supported SQL subset, joins/subqueries edge cases beyond Table II relational-algebra rules: **incomplete**.
- How complex predicates that are *not* PK/unique equality are classified (paper intentionally coarsens to CAS/TAS): OK conceptually; exact fallback rules for T-SQL dialects: **not given**.
- Stored-proc R/W extraction tooling: “upon definition” — format/admin UX unspecified. [Table IV]

### TSID / commit ordering
- Exact API used to read commit timestamps from the engine (PostgreSQL in eval): **not specified**.
- Behavior under SQL Server (no identical “commit timestamp” exposure as assumed): **out of paper scope** → project adaptation needed.

### Transactions Buffer
- Memcached key layout, ordering guarantees, durability, eviction, and multi-consumer coordination: **not specified** beyond “distributedly stored in memory using Memcached”. [p. 3]
- Whether Buffer must preserve total order visible to all Extractors the same way: implied by TSID order, but ops details missing.

### Load metric
- Definition of “least-loaded” Extension (queue length, CPU, open queries, …): **not specified**. [Challenge 2]

### Indexes
- CIndex/RIndex key encoding, multiPut semantics with concurrent updates, and exact RIndex pruning policy (“periodically”): **underspecified**. [§5.1]
- Index state rebuild after TM failover: implied by “rebuilds its internal state” but steps incomplete. [§6.1]

### 1SR mode
- Sync protocol details (barrier per statement vs per txn; client ack timing; failure during sync): only one sentence. [§5.3]

### Backup / Seed
- Cross-engine backup orchestration beyond “use engine utilities” + associate TSID: examples for PG/Oracle only; **SQL Server procedure not in paper**. [§6.1]
- Incremental backup cadence policy: modeled as future self-management, not a shipped algorithm. [§6.3]

### Delivery Agent vs TM
- Whether reads always bypass TM after routing decision, auth, cancellation, and multi-result transactions: lightly described. [§III]

### Unsupported features
- Non-deterministic procedures/triggers: explicitly unsupported — no workaround algorithm given. [Table IV]
- Heterogeneous engines: “could support in the future” — **not a delivered claim**. [§III]

### Correctness under SQL Server isolation
- Theorem 1 assumes each replica provides **Strong SI**. SQL Server default isolation is often Read Committed; SI is optional (`ALLOW_SNAPSHOT_ISOLATION` / `SNAPSHOT`). Whether paper’s GSSI proof transfers unchanged under RCSI or locking RC: **not addressed** (engine was PostgreSQL in eval). [§5.3, §VII]

### Security / multi-tenancy / DDL concurrency
- AuthZ, encryption, multi-database hosting, concurrent DDL with routing: **not covered**.

## Explicit reimplementation decisions (resolved for MVP)
1. **Buffer:** `InMemoryTransactionsBuffer` (in-process). Distributed Memcached/Redis still open for multi-host.
2. **Commit order:** `SYSUTCDATETIME()` before commit + `TsIdAssigner` sort (documented adaptation).
3. **Client shape:** `HihooiClient` / `HihooiSession` library API (not TDS interceptor).
4. **Consistency modes:** all four implemented (Gssi, WeakSi, RsiPc, OneCopySerializability).
5. **Least-loaded metric:** inflight read lease count per Extension (adaptation; paper unspecified).
6. **RSI-PC unbounded wait:** timeout then Primary fallback (`RsiPcWaitTimeout`).

## Resolution process
When answered, move the fact into the owning doc (`Architecture` / `Algorithms` / `Replication` / `Implementation-Plan`) with citation **or** label `Adaptation:` if project-specific.
