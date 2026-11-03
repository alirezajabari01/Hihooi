# Algorithms

**Source:** `References/hihooi.pdf` §IV–V (pp. 4–9). Pseudocode summarized; refer to paper for exact listings.

## Prerequisites: R/W sets & affecting classes
For each SQL statement (and union for multi-statement txn):
- **Table / Column / Row** read sets and write sets. Row sets only for **PK or unique-key equality** predicates (parser simplicity). [pp. 4–5 §4.1; Table II]
- **Affecting class** per table R: **RAS** (specific rows), **CAS** (some columns), **TAS** (all columns / whole table). DDL → TAS. [p. 5]
- Multi-statement class for R: all RAS → RAS; all CAS → CAS; else TAS. [p. 5]

## TState & TSID
TState fields: TSID; ordered SQL write statements; per-statement + overall R/W sets; Primary execution time; commit|rollback. [p. 5 §4.2]

**TSID:** unique sequential ID assigned when txn **completes** on Primary, ordered by engine **commit timestamps**. TM may reorder out-of-order commit responses (paper: ≤~2% in heavy write tests) before issuing TSIDs. TState needs TSID before Extension replay. [p. 5]

## Algorithm 1 — `areIndependent(ts1, ts2)` [p. 6]
Independent if table write/rw sets do not conflict across the pair; else for each common table, independent only if both CAS with disjoint column w/rw sets, or both RAS with disjoint row w/rw sets. Else conflict.

Used for: parallel apply on Extensions; statement-level independence inside multi-statement writes.

## Algorithm 2 — Parallel apply at Extractor [p. 6]
State: `runningState`, `waitingState`, FIFO `waitQueue`.

- **onNewTransaction(tsNew):** if independent of running **and** waiting → merge into running + execute; else merge into waiting + enqueue.
- **onTransactionComplete(tsOld):** remove from running; while head of waitQueue independent of running → dequeue, move to running, execute.

R/W sets implemented as hash maps with **refcounts** for merge/remove. [p. 6]

**Progress report:** Extractor reports latest applied TSID to TM **contiguously (no gaps)**. [p. 6]

## Algorithm 3 — `updateIndexes(ts)` after write completes [p. 8]
TM indexes:
- **TIndex:** table → last modifying TSID
- **CIndex:** column → last TSID (for TAS/CAS on that table)
- **RIndex:** row key → last TSID (for RAS); prune entries applied on all replicas periodically. [p. 8]

## Algorithm 4 — `findLatestConsistentTSID(ts)` for a read [p. 8]
For each table in read set (if in TIndex):
- TAS → max with TIndex[t]
- CAS → max with CIndex[cols] and RIndex.maxValue(t)
- RAS → max with RIndex[rows] and CIndex[cols]
Return global max. Any Extension with applied TSID ≥ that value may run the read; else Primary. [pp. 7–8]

## Load balancing among eligible Extensions
If multiple Extensions qualify → send to **least-loaded** Extension. [p. 2 Challenge 2]

## Statement-level LB (multi-statement write) [p. 8 §5.2]
TM keeps a running write state for `Tm`. Independent read statements (Alg. 1 vs running writes) may use Alg. 4 → Extension under **SI/GSSI** (not claimed for serializable). Dependent / no replica → Primary.

## Consistency modes (session granularity) [p. 9 §5.3]
| Mode | Writes to Extensions | Reads |
|---|---|---|
| **GSSI (default Hihooi)** | Async + Alg. 2 | Alg. 4 / Primary fallback |
| Weak SI | Async | Any Extension (ignore lag) |
| RSI-PC | Async | Only fully caught-up Extension (wait if none) |
| 1SR | **Synchronous** to all | Any Extension |

**Theorem 1:** If each replica engine guarantees Strong SI, Hihooi guarantees Global Strong SI (proof sketch in paper). [p. 9]
