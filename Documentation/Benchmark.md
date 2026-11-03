# Benchmark

Tool: `benchmarks/Hihooi.Benchmark`

```bash
dotnet run --project benchmarks/Hihooi.Benchmark -c Release
```

Measures **in-process** Alg.1–4 micro-latency (throughput, avg, p95, p99).

**Does not** invent SQL Server cluster numbers. For end-to-end DB benchmarks, use Sample + live SQL Server under load and record separately.
