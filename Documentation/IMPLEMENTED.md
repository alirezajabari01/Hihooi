# Implemented vs Remaining

## Implemented
- Core Alg.1–4 + contiguous TSID + indexes + routing
- Consistency: GSSI, Weak SI, RSI-PC, 1SR
- SQL Server provider (Snapshot, timeouts, SI verify)
- Safe analyzer fallbacks (JOIN/CTE/subquery/EXEC/…)
- Metrics + Dashboard (SignalR/REST)
- File recovery journal
- Redis distributed buffer package
- Sample + micro-benchmark
- NuGet: Hihooi, Core, SqlServer, Redis
- Tests: 32 passed (local)

## Partial / incomplete
- Full Seed/Primary failover automation
- Multi-TM HA fencing
- Live Redis/SQL Server CI (env-gated)
- End-to-end SQL Server cluster throughput benchmarks
