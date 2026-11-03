# Architecture

**Source:** `References/hihooi.pdf` §II–III (pp. 2–3), Fig. 1; §VI (pp. 9–10).

## Topology
Master–slave full replication:
- **Primary DB** — all write transactions (and fallback reads).
- **Extension DBs** — read traffic when routing deems them consistent for the accessed data; also apply replicated writes.
- **Seed DB** — consistent checkpoint/backup of Primary at a known TSID; used to spawn Extension DBs and as hot-standby candidate. [p. 3 Challenge 3; p. 10 §6.1]

Hihooi sits **between** applications and engines; engines need not be modified. Paper client path: custom JDBC/ODBC → Hihooi → stock JDBC to engines. [p. 3 §III]

## Logical components

| Component | Role | Source |
|---|---|---|
| **Hihooi Driver (JDBC/ODBC)** | App-facing API; replace stock driver only. | p. 3 §III |
| **Transaction Manager (TM)** | Intercept queries; classify read vs write; session mgmt; route writes→Primary, reads→consistent Extension (or Primary); track per-Extension applied TSID; build TStates; update hash indexes; push TStates to Transactions Buffer. | pp. 3, 5, 7 |
| **Transactions Buffer** | In-memory distributed store (paper: Memcached) for completed TStates; async fan-out to Extractors. | p. 3 |
| **Extractor** (per Extension) | Pull TStates; parallel apply (Alg. 2); report contiguous applied TSID to TM. | pp. 3, 5–6 |
| **Delivery Agent** (per Extension) | Execute routed read-only queries; stream result sets to client (avoid bottleneck at TM). | p. 3 |
| **Archiver** | (1) Drive Seed DB backups + associate last committed TSID; (2) Move TStates already applied on **all** Extensions from Transactions Buffer → persistent **Archiver Buffer** (text files); prune after Seed includes them. | pp. 3, 10 |
| **HController** | Cluster mgmt: add/remove Extension DBs, checkpoints, coordination. | p. 3 |
| **HConsole** | Interactive config/management/query console. | p. 3 |

## Eval placement (not mandatory design)
Paper cluster: TM on Primary node; TM backup + Archiver on Seed node; Transactions Buffer on an Extension node. [p. 11 §VII]

## Engine coupling
Middleware talks to engines via standard drivers → multi-vendor / multi-version possible; heterogeneous engines “could” be supported in future (not claimed as delivered). [p. 3]

## Consistency contract (architecture-level)
Default illusion of one SI database → **Global Strong Snapshot Isolation (GSSI)**, assuming each replica engine provides Strong SI. Optional per-session: Weak SI, RSI-PC, 1SR. Details: Algorithms + Transaction-Flow docs; formalization §5.3 p. 9.
