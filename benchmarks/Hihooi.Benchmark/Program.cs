using System.Diagnostics;
using Hihooi.Core.Algorithms;
using Hihooi.Core.Model;
using Hihooi.Core.Indexes;
using Hihooi.Core.Parsing;

Console.WriteLine("Hihooi Micro-Benchmark (in-process algorithms; no fake DB throughput claims)");
Console.WriteLine("For SQL Server end-to-end latency, run with live env + Sample under load.");

var analyzer = new LightweightSqlAnalyzer();
var idx = new ReplicaIndexes();
var rng = new Random(42);

static TransactionState MakeWrite(LightweightSqlAnalyzer a, string sql)
{
    var info = a.Analyze(sql);
    var ts = new TransactionState();
    ts.MergeStatement(info);
    return ts;
}

// Warmup
for (var i = 0; i < 1000; i++)
    _ = IndependenceAnalyzer.AreIndependent(
        MakeWrite(analyzer, "UPDATE R SET A2=1 WHERE A1=1").Sets,
        MakeWrite(analyzer, "UPDATE S SET B2=1 WHERE B1=2").Sets);

void Bench(string name, int n, Action action)
{
    var sw = Stopwatch.StartNew();
    var lat = new List<double>(n);
    for (var i = 0; i < n; i++)
    {
        var one = Stopwatch.StartNew();
        action();
        one.Stop();
        lat.Add(one.Elapsed.TotalMilliseconds);
    }
    sw.Stop();
    lat.Sort();
    double Pct(double p) => lat[(int)Math.Clamp(p / 100.0 * (lat.Count - 1), 0, lat.Count - 1)];
    Console.WriteLine($"{name}: n={n} thr={n / sw.Elapsed.TotalSeconds:F0}/s avg={lat.Average():F4}ms p95={Pct(95):F4}ms p99={Pct(99):F4}ms");
}

Bench("Alg1 independent tables", 20000, () =>
{
    IndependenceAnalyzer.AreIndependent(
        MakeWrite(analyzer, "UPDATE R SET A2=1 WHERE A1=1").Sets,
        MakeWrite(analyzer, "UPDATE S SET B2=1 WHERE B1=2").Sets);
});

Bench("Alg1 conflicting same row", 20000, () =>
{
    IndependenceAnalyzer.AreIndependent(
        MakeWrite(analyzer, "UPDATE R SET A2=1 WHERE A1=1").Sets,
        MakeWrite(analyzer, "UPDATE R SET A3=2 WHERE A1=1").Sets);
});

Bench("Alg2 schedule 5 tx mix", 5000, () =>
{
    var sched = new ParallelApplyScheduler();
    var txs = new[]
    {
        MakeWrite(analyzer, "UPDATE R SET A2=1 WHERE A1=1"),
        MakeWrite(analyzer, "UPDATE S SET B2=1 WHERE B5>0"),
        MakeWrite(analyzer, "UPDATE R SET A3=1 WHERE A2<5"),
        MakeWrite(analyzer, "DELETE FROM R WHERE A1=120"),
        MakeWrite(analyzer, "UPDATE S SET B4=1 WHERE B5<9")
    };
    for (var i = 0; i < txs.Length; i++) txs[i].TsId = i + 1;
    var started = new List<TransactionState>();
    foreach (var t in txs) started.AddRange(sched.OnNewTransaction(t));
    foreach (var t in started.ToList())
        foreach (var n in sched.OnTransactionComplete(t)) started.Add(n);
});

Bench("Alg3+4 index update/lookup", 10000, () =>
{
    var w = MakeWrite(analyzer, $"UPDATE R SET A2={rng.Next(1,9)} WHERE A1={rng.Next(1,50)}");
    idx.UpdateIndexes(w.Sets, rng.Next(1, 100000));
    var r = analyzer.Analyze($"SELECT A2 FROM R WHERE A1={rng.Next(1,50)}");
    _ = idx.FindLatestConsistentTsId(r.Sets);
});

Console.WriteLine("Done. Results are local CPU microbench only — not SQL Server cluster throughput.");
