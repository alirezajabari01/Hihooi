# Replication

**Source:** `References/hihooi.pdf` §IV, §VI (pp. 5–7, 9–10); Table IV p. 6.

## Model
- **Statement replication** (not row-based): replay SQL write statements on Extensions.
- Propagation **asynchronous** w.r.t. Primary commit latency (except optional **1SR** session mode = sync apply to all Extensions). [pp. 2, 6–7, 9]
- Consistency of apply order: TSIDs reflect Primary completion order; parallelize only **independent** TStates (Alg. 1–2). Serial TSID order is sufficient but slow. [p. 5]

## Why statement (paper rationale)
Avoid engine hooks/triggers for row capture; multi-row UPDATE/DELETE cheap to ship; R/W sets derived from statements feed both apply and routing. Tradeoff: non-determinism must be neutralized. [pp. 6–7 §4.3]

## Practical non-determinism (Table IV) [p. 6]
| Issue | Paper resolution |
|---|---|
| Sequences / identity | Replay **failed** txns too so sequences advance; sequence ops in “reads” → treat as writes (Primary + replicate). |
| `now` / `current_timestamp` | Rewrite to concrete value shared by all replicas. |
| UDFs | Non-deterministic: execute once, substitute return value; deterministic: leave as-is. |
| Procedures / triggers | Deterministic: extract R/W at definition; non-SQL deterministic: admin supplies table R/W; **non-deterministic not supported**. |

## Buffer pipeline
```
Primary commit → TM TState → Transactions Buffer (Memcached in paper)
                              ↓
                         Extractors (all Extensions)
                              ↓ (when applied on ALL Extensions)
                         Archiver Buffer (persistent text files)
```
Seed creation associates **last included TSID**; Archiver drops Archiver-Buffer entries already in Seed. [pp. 3, 10]

## Elasticity: add Extension [p. 10 §6.2]
1. Clone/replicate **Seed DB** onto new node.
2. Replay Archiver Buffer via Alg. 2.
3. Register with TM; serve reads that Alg. 4 allows; continue catching up from Transactions Buffer.
No global barrier / no borrowing resources from active replicas (paper claim).

## Remove / fail Extension
TM stops routing there; remaining Extensions serve reads. Extractors log completed applies; re-add resumes replay from that point. [p. 10]

## Fault recovery (paper) [p. 10 §6.1]
| Failure | Recovery sketch |
|---|---|
| **Primary** | Replay both buffers onto Seed; promote Seed as write primary (hot standby role). |
| **Archiver / Archiver Buffer** | Recreate Seed from Primary → reset Archiver Buffer. |
| **Transactions Buffer** | Extensions pause new applies but serve consistent reads; on recovery TM re-pushes retained TStates (TM deletes TState only after all Extensions applied). |
| **TM** | Launch TM on backup; rebuild from Transactions Buffer + Primary status. If Primary commits missing from buffer → new Seed from Primary, apply to all Extensions, then resume. Same if TM + Buffer fail together. |

Backup mechanism: use **engine backup utilities** + checkpoint tied to TSID (PostgreSQL restore point / Oracle online backup+redo examples in paper). [p. 9–10]
