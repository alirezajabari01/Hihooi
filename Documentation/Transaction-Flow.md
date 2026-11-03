# Transaction Flow

**Source:** `References/hihooi.pdf` §II–V (pp. 2–9). Algorithms detailed in `Algorithms.md`.

## Classification
- **Write transaction:** ≥1 modifying statement (INSERT/UPDATE/DELETE; also sequence-affecting reads treated as writes — see Replication practical issues).
- **Read transaction:** otherwise. [p. 2 §II]

Single-statement auto-commit vs multi-statement (`begin`/autocommit off) follow engine conventions; Hihooi mirrors them. [p. 4 §4.1]

## Write path
1. Client → Hihooi Driver → **TM**.
2. TM parses statement → R/W sets + affecting class; executes writes on **Primary DB**.
3. On completion: obtain commit timestamps (incl. concurrent committers); assign **TSID(s)** in commit-timestamp order; finalize **TState**.
4. TM **updateIndexes** (Alg. 3); push TState → **Transactions Buffer**.
5. Each Extension **Extractor** consumes TStates in arrival/order; applies via Alg. 2 (commit or rollback as recorded).
6. Extractor notifies TM of contiguous applied TSID; TM map Extension → applied TSID.
7. TM retains TState until applied on **all** Extensions; Archiver later moves universally-applied TStates to Archiver Buffer. [pp. 3, 5–6, 10]

## Read-only path (default GSSI)
1. TM builds read sets / class.
2. Alg. 4 → required TSID.
3. Eligible Extensions = those with applied TSID ≥ required; pick least-loaded → **Delivery Agent** executes and streams results.
4. If none eligible → **Primary**. [pp. 2, 7–8]

## Multi-statement write with internal reads (§5.2)
1. Writes stay on Primary; TM merges them into session **running write state**.
2. Each internal read: if independent of running writes → same as read-only routing; else Primary.
3. Own-writes visibility for dependent reads stays on Primary. [p. 8]

## Session consistency options
Client/session may select Weak SI / RSI-PC / 1SR instead of default GSSI; changes wait/sync behavior (see Algorithms table). [p. 9]

## Client / result delivery
Delivery Agent returns result sets **incrementally** to the client to avoid TM becoming a result bottleneck. [p. 3]
