using System.Collections.Concurrent;

namespace Hihooi.Core.Diagnostics;

/// <summary>Process-local metrics for monitoring (no synthetic data).</summary>
public sealed class HihooiMetrics
{
    private long _commits;
    private long _rollbacks;
    private long _failedApplies;
    private long _primaryReads;
    private long _extensionReads;
    private long _primaryFallbacks;
    private long _conflictsDetected;
    private long _parallelAppliesStarted;

    public ConcurrentDictionary<string, ExtensionMetrics> Extensions { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentQueue<string> RecentErrors { get; } = new();
    public ConcurrentDictionary<string, long> ReadsByExtension { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentDictionary<string, long> RouteReasons { get; } = new(StringComparer.OrdinalIgnoreCase);

    public long Commits => Interlocked.Read(ref _commits);
    public long Rollbacks => Interlocked.Read(ref _rollbacks);
    public long FailedApplies => Interlocked.Read(ref _failedApplies);
    public long PrimaryReads => Interlocked.Read(ref _primaryReads);
    public long ExtensionReads => Interlocked.Read(ref _extensionReads);
    public long PrimaryFallbacks => Interlocked.Read(ref _primaryFallbacks);
    public long ConflictsDetected => Interlocked.Read(ref _conflictsDetected);
    public long ParallelAppliesStarted => Interlocked.Read(ref _parallelAppliesStarted);
    public DateTime StartedUtc { get; } = DateTime.UtcNow;

    public void RecordCommit() => Interlocked.Increment(ref _commits);
    public void RecordRollback() => Interlocked.Increment(ref _rollbacks);
    public void RecordFailedApply() => Interlocked.Increment(ref _failedApplies);
    public void RecordPrimaryRead(string reason)
    {
        Interlocked.Increment(ref _primaryReads);
        RouteReasons.AddOrUpdate(reason, 1, (_, v) => v + 1);
    }
    public void RecordExtensionRead(string extensionId, string reason)
    {
        Interlocked.Increment(ref _extensionReads);
        ReadsByExtension.AddOrUpdate(extensionId, 1, (_, v) => v + 1);
        RouteReasons.AddOrUpdate(reason, 1, (_, v) => v + 1);
    }
    public void RecordPrimaryFallback(string reason)
    {
        Interlocked.Increment(ref _primaryFallbacks);
        RecordPrimaryRead(reason);
    }
    public void RecordConflict() => Interlocked.Increment(ref _conflictsDetected);
    public void RecordParallelApply() => Interlocked.Increment(ref _parallelAppliesStarted);

    public void RecordError(string message)
    {
        RecentErrors.Enqueue($"{DateTime.UtcNow:O} {message}");
        while (RecentErrors.Count > 200 && RecentErrors.TryDequeue(out _)) { }
    }

    public void UpsertExtension(string id, Action<ExtensionMetrics> update)
    {
        var m = Extensions.GetOrAdd(id, _ => new ExtensionMetrics { Id = id });
        lock (m) update(m);
    }

    public HihooiMetricsSnapshot Snapshot()
    {
        return new HihooiMetricsSnapshot
        {
            StartedUtc = StartedUtc,
            Commits = Commits,
            Rollbacks = Rollbacks,
            FailedApplies = FailedApplies,
            PrimaryReads = PrimaryReads,
            ExtensionReads = ExtensionReads,
            PrimaryFallbacks = PrimaryFallbacks,
            ConflictsDetected = ConflictsDetected,
            ParallelAppliesStarted = ParallelAppliesStarted,
            Extensions = Extensions.Values.Select(e => e.Clone()).ToList(),
            ReadsByExtension = ReadsByExtension.ToDictionary(kv => kv.Key, kv => kv.Value),
            RouteReasons = RouteReasons.ToDictionary(kv => kv.Key, kv => kv.Value),
            RecentErrors = RecentErrors.ToArray()
        };
    }
}

public sealed class ExtensionMetrics
{
    public string Id { get; init; } = "";
    public bool IsOnline { get; set; } = true;
    public long AppliedTsId { get; set; }
    public long Lag { get; set; }
    public int InflightReads { get; set; }
    public long ApplyFailures { get; set; }

    public ExtensionMetrics Clone()
    {
        lock (this)
        {
            return new ExtensionMetrics
            {
                Id = Id,
                IsOnline = IsOnline,
                AppliedTsId = AppliedTsId,
                Lag = Lag,
                InflightReads = InflightReads,
                ApplyFailures = ApplyFailures
            };
        }
    }
}

public sealed class HihooiMetricsSnapshot
{
    public DateTime StartedUtc { get; init; }
    public long Commits { get; init; }
    public long Rollbacks { get; init; }
    public long FailedApplies { get; init; }
    public long PrimaryReads { get; init; }
    public long ExtensionReads { get; init; }
    public long PrimaryFallbacks { get; init; }
    public long ConflictsDetected { get; init; }
    public long ParallelAppliesStarted { get; init; }
    public IReadOnlyList<ExtensionMetrics> Extensions { get; init; } = Array.Empty<ExtensionMetrics>();
    public IReadOnlyDictionary<string, long> ReadsByExtension { get; init; } = new Dictionary<string, long>();
    public IReadOnlyDictionary<string, long> RouteReasons { get; init; } = new Dictionary<string, long>();
    public IReadOnlyList<string> RecentErrors { get; init; } = Array.Empty<string>();
}
