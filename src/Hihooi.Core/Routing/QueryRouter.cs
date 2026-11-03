using Hihooi.Core.Consistency;
using Hihooi.Core.Diagnostics;
using Hihooi.Core.Indexes;
using Hihooi.Core.Model;

namespace Hihooi.Core.Routing;

public enum RouteTargetKind
{
    Primary,
    Extension
}

public sealed class RouteDecision
{
    public required RouteTargetKind Target { get; init; }
    public string? ExtensionId { get; init; }
    public string? ConnectionString { get; init; }
    public long RequiredTsId { get; init; }
    public string Reason { get; init; } = "";
}

public sealed class QueryRouter
{
    private readonly ReplicaIndexes _indexes;
    private readonly ExtensionRegistry _extensions;
    private readonly Func<long> _latestPrimaryTsId;
    private readonly HihooiMetrics? _metrics;

    public QueryRouter(
        ReplicaIndexes indexes,
        ExtensionRegistry extensions,
        Func<long> latestPrimaryTsId,
        HihooiMetrics? metrics = null)
    {
        _indexes = indexes;
        _extensions = extensions;
        _latestPrimaryTsId = latestPrimaryTsId;
        _metrics = metrics;
    }

    public RouteDecision RouteRead(ReadWriteSets readSets, ConsistencyLevel level, TimeSpan? rsiWait = null)
    {
        return level switch
        {
            ConsistencyLevel.WeakSi => RouteAnyExtensionOrPrimary("weak-si"),
            ConsistencyLevel.RsiPc => RouteFullyConsistent(rsiWait ?? TimeSpan.FromSeconds(5)),
            ConsistencyLevel.OneCopySerializability => RouteAnyExtensionOrPrimary("1sr-any-extension"),
            _ => RouteGssi(readSets)
        };
    }

    private RouteDecision RouteGssi(ReadWriteSets readSets)
    {
        var required = _indexes.FindLatestConsistentTsId(readSets);
        var ext = _extensions.SelectLeastLoaded(e => e.AppliedTsId >= required);
        if (ext is null)
        {
            return new RouteDecision
            {
                Target = RouteTargetKind.Primary,
                RequiredTsId = required,
                Reason = required == 0 ? "gssi-no-extension" : "gssi-no-consistent-replica"
            };
        }

        return new RouteDecision
        {
            Target = RouteTargetKind.Extension,
            ExtensionId = ext.Id,
            ConnectionString = ext.ConnectionString,
            RequiredTsId = required,
            Reason = "gssi-consistent-least-loaded"
        };
    }

    private RouteDecision RouteAnyExtensionOrPrimary(string reason)
    {
        var ext = _extensions.SelectLeastLoaded(_ => true);
        if (ext is null)
        {
            return new RouteDecision
            {
                Target = RouteTargetKind.Primary,
                RequiredTsId = 0,
                Reason = reason + "-fallback-primary"
            };
        }

        return new RouteDecision
        {
            Target = RouteTargetKind.Extension,
            ExtensionId = ext.Id,
            ConnectionString = ext.ConnectionString,
            RequiredTsId = 0,
            Reason = reason
        };
    }

    private RouteDecision RouteFullyConsistent(TimeSpan wait)
    {
        var latest = _latestPrimaryTsId();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < wait)
        {
            var ext = _extensions.SelectLeastLoaded(e => e.AppliedTsId >= latest);
            if (ext is not null)
            {
                return new RouteDecision
                {
                    Target = RouteTargetKind.Extension,
                    ExtensionId = ext.Id,
                    ConnectionString = ext.ConnectionString,
                    RequiredTsId = latest,
                    Reason = "rsi-pc-fully-consistent"
                };
            }
            Thread.Sleep(10);
        }

        return new RouteDecision
        {
            Target = RouteTargetKind.Primary,
            RequiredTsId = latest,
            Reason = "rsi-pc-timeout-fallback-primary"
        };
    }
}
