# Failure Recovery

## Journal
Optional `FileRecoveryJournal` when `HihooiOptions.RecoveryJournalDirectory` is set.

Statuses: `PendingPublish` → `Published` → `AppliedEverywhere` / `Failed`

On `StartAsync`, pending/published entries are republished to the buffer for Extractor catch-up.

## Scenarios

| Scenario | Behavior |
|---|---|
| Extension apply fail | Scheduler slot freed; Extension offline; contiguous TSID not advanced |
| Extension offline (1SR) | Commit throws |
| Extension offline (GSSI) | Reads fall back to Primary / other replicas |
| Primary connection fail | Error surfaced; metrics recorded |
| Process crash | Journal republish on restart (if enabled) |
| Redis disconnect | StackExchange.Redis reconnect; subscribe resumes from cursor |
| Rollback | TState published with Rollback; indexes not updated |

## Not fully automated
Primary failover to Seed DB promotion (paper §6) is **not** fully implemented as an ops control plane.
