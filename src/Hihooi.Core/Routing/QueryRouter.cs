using Hihooi.Core.Consistency;
using Hihooi.Core.Diagnostics;
using Hihooi.Core.Diagnostics.Health;
using Hihooi.Core.Indexes;
using Hihooi.Core.Model;

namespace Hihooi.Core.Routing;

public enum RouteTargetKind
{
    Primary,
    Extension
}

public sealed class RouteCandidateEvaluation
{
    public required string NodeId { get; init; }
    public bool Eligible { get; init; }
    public bool Selected { get; init; }
    public string? RejectReason { get; init; }
    public long AppliedTsId { get; init; }
    public int InflightReads { get; init; }
    public string HealthStatus { get; init; } = "Unknown";
    public double? HealthLatencyMs { get; init; }
}

public sealed class RouteDecision
{
    public required RouteTargetKind Target { get; init; }
    public string? ExtensionId { get; init; }
    public string? ConnectionString { get; init; }
    public long RequiredTsId { get; init; }
    public string Reason { get; init; } = "";
    public IReadOnlyList<RouteCandidateEvaluation> Candidates { get; init; } = Array.Empty<RouteCandidateEvaluation>();
}

public sealed class QueryRouter
{
    private readonly ReplicaIndexes _indexes;
    private readonly ExtensionRegistry _extensions;
    private readonly Func<long> _latestPrimaryTsId;
    private readonly HihooiMetrics? _metrics;
    private readonly INodeHealthMonitor? _health;
    private int _roundRobin;

    public QueryRouter(
        ReplicaIndexes indexes,
        ExtensionRegistry extensions,
        Func<long> latestPrimaryTsId,
        HihooiMetrics? metrics = null,
        INodeHealthMonitor? health = null)
    {
        _indexes = indexes;
        _extensions = extensions;
        _latestPrimaryTsId = latestPrimaryTsId;
        _metrics = metrics;
        _health = health;
    }

    public RouteDecision RouteRead(ReadWriteSets readSets, ConsistencyLevel level, TimeSpan? rsiWait = null)
    {
        return level switch
        {
            ConsistencyLevel.WeakSi => RouteAnyExtensionOrPrimary("weak-si", _ => true),
            ConsistencyLevel.RsiPc => RouteFullyConsistent(rsiWait ?? TimeSpan.FromSeconds(5)),
            ConsistencyLevel.OneCopySerializability => RouteAnyExtensionOrPrimary("1sr-any-extension", _ => true),
            _ => RouteGssi(readSets)
        };
    }

    private RouteDecision RouteGssi(ReadWriteSets readSets)
    {
        var required = _indexes.FindLatestConsistentTsId(readSets);
        return RouteAnyExtensionOrPrimary(
            required == 0 ? "gssi" : "gssi-consistent-least-loaded",
            e => e.AppliedTsId >= required,
            requiredTsId: required,
            lagReject: "applied-tsid-below-required",
            noExtensionReason: required == 0 ? "gssi-no-extension" : "gssi-no-consistent-replica");
    }

    private RouteDecision RouteAnyExtensionOrPrimary(
        string reason,
        Func<ExtensionInfo, bool> predicate,
        long requiredTsId = 0,
        string lagReject = "predicate-not-met",
        string? noExtensionReason = null)
    {
        var all = _extensions.Snapshot();
        var evaluations = new List<RouteCandidateEvaluation>(all.Count);
        var ordered = all.OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase).ToList();
        var eligibleIds = new List<string>();

        foreach (var e in ordered)
        {
            var healthStatus = _health?.GetStatus(e.Id) ?? NodeHealthStatus.Unknown;
            var healthLatency = _health?.GetLatencyMs(e.Id);
            string? reject = null;
            var eligible = true;

            if (!e.IsOnline)
            {
                eligible = false;
                reject = "offline";
            }
            else if (healthStatus is NodeHealthStatus.Unhealthy or NodeHealthStatus.Offline)
            {
                eligible = false;
                reject = "health-" + healthStatus.ToString().ToLowerInvariant();
            }
            else if (!predicate(e))
            {
                eligible = false;
                reject = lagReject;
            }

            if (eligible)
                eligibleIds.Add(e.Id);

            evaluations.Add(new RouteCandidateEvaluation
            {
                NodeId = e.Id,
                Eligible = eligible,
                Selected = false,
                RejectReason = reject,
                AppliedTsId = e.AppliedTsId,
                InflightReads = e.InflightReads,
                HealthStatus = healthStatus.ToString(),
                HealthLatencyMs = healthLatency
            });
        }

        if (eligibleIds.Count == 0)
        {
            return new RouteDecision
            {
                Target = RouteTargetKind.Primary,
                RequiredTsId = requiredTsId,
                Reason = noExtensionReason ?? reason + "-fallback-primary",
                Candidates = evaluations
            };
        }

        var turn = Interlocked.Increment(ref _roundRobin);
        var winnerId = eligibleIds[(turn - 1) % eligibleIds.Count];
        var winner = ordered.First(e => string.Equals(e.Id, winnerId, StringComparison.OrdinalIgnoreCase));

        for (var i = 0; i < evaluations.Count; i++)
        {
            if (string.Equals(evaluations[i].NodeId, winner.Id, StringComparison.OrdinalIgnoreCase))
            {
                evaluations[i] = new RouteCandidateEvaluation
                {
                    NodeId = evaluations[i].NodeId,
                    Eligible = true,
                    Selected = true,
                    RejectReason = null,
                    AppliedTsId = evaluations[i].AppliedTsId,
                    InflightReads = evaluations[i].InflightReads,
                    HealthStatus = evaluations[i].HealthStatus,
                    HealthLatencyMs = evaluations[i].HealthLatencyMs
                };
            }
            else if (evaluations[i].Eligible && evaluations[i].RejectReason is null)
            {
                evaluations[i] = new RouteCandidateEvaluation
                {
                    NodeId = evaluations[i].NodeId,
                    Eligible = true,
                    Selected = false,
                    RejectReason = "round-robin-waiting-turn",
                    AppliedTsId = evaluations[i].AppliedTsId,
                    InflightReads = evaluations[i].InflightReads,
                    HealthStatus = evaluations[i].HealthStatus,
                    HealthLatencyMs = evaluations[i].HealthLatencyMs
                };
            }
        }

        return new RouteDecision
        {
            Target = RouteTargetKind.Extension,
            ExtensionId = winner.Id,
            ConnectionString = winner.ConnectionString,
            RequiredTsId = requiredTsId,
            Reason = reason + "-round-robin",
            Candidates = evaluations
        };
    }

    private RouteDecision RouteFullyConsistent(TimeSpan wait)
    {
        var latest = _latestPrimaryTsId();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        RouteDecision? last = null;
        while (sw.Elapsed < wait)
        {
            last = RouteAnyExtensionOrPrimary(
                "rsi-pc-fully-consistent",
                e => e.AppliedTsId >= latest,
                requiredTsId: latest,
                lagReject: "not-caught-up",
                noExtensionReason: "rsi-pc-waiting");
            if (last.Target == RouteTargetKind.Extension)
                return last;
            Thread.Sleep(10);
        }

        last ??= RouteAnyExtensionOrPrimary(
            "rsi-pc-timeout-fallback-primary",
            e => e.AppliedTsId >= latest,
            requiredTsId: latest,
            lagReject: "not-caught-up",
            noExtensionReason: "rsi-pc-timeout-fallback-primary");

        return new RouteDecision
        {
            Target = RouteTargetKind.Primary,
            RequiredTsId = latest,
            Reason = "rsi-pc-timeout-fallback-primary",
            Candidates = last.Candidates
        };
    }
}
