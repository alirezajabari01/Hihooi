# Distributed Buffer

## Interface
`ITransactionsBuffer` in Core:
- `PublishAsync(TransactionState)`
- `SubscribeAsync(consumerId)`
- `AcknowledgeAppliedAsync(consumerId, contiguousTsId)`

## In-Memory
`InMemoryTransactionsBuffer` — default for single-process.

## Redis
Package: `Hihooi.Redis` → `RedisTransactionsBuffer` (StackExchange.Redis)

- Sorted set keyed by TSID (total order)
- Per-consumer cursor for contiguous ack
- TTL/expiration on keys
- Idempotent republish (same TSID)
- Polling subscribe (reconnect resumes from cursor)

### Register
```csharp
services.AddHihooiRedisBuffer(options); // before or with AddHihooi; replaces ITransactionsBuffer
services.AddHihooi(o => { o.RedisConnectionString = "..."; /* still need explicit AddHihooiRedisBuffer */ });
```

Recommended:
```csharp
services.AddHihooi(o => { ... });
services.AddHihooiRedisBuffer(connectionString);
```
(Register Redis buffer **after** AddHihooi only if AddHihooi did not already register InMemory — current AddHihooi skips if `ITransactionsBuffer` already registered. Prefer: call `AddHihooiRedisBuffer` **before** `AddHihooi`.)

## HA limitation
No multi-TM leader election / fencing. Multiple publishers may append; Extractors dedupe by TSID. Full multi-host HA is **not** claimed complete.
