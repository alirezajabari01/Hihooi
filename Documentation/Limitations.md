# Limitations

## vs paper
- Client is .NET library API, not JDBC/ODBC drop-in driver
- Buffer default is in-memory; Redis optional (no Memcached)
- Seed DB auto backup/promote control plane incomplete
- TSID uses pre-commit `SYSUTCDATETIME()` ordering (adaptation)
- RSI-PC waits with timeout then Primary fallback
- SQL analyzer is conservative, not a full T-SQL semantic engine
- GSSI proof assumes Strong SI; SQL Server SI ≠ PostgreSQL SI identically

## Operational
- Multi-host TM fencing not implemented
- Dashboard without SQL env shows Not Available (by design)
- Live SQL Server / Redis tests require external infrastructure
