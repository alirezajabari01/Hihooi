using Hihooi.Core.Consistency;
using Hihooi.Core.Diagnostics;
using Hihooi.Core.Indexes;
using Hihooi.Core.Model;
using Hihooi.Core.Routing;

namespace Hihooi.Tests.Consistency;

public class ConsistencyModeRoutingTests
{
    private static (QueryRouter router, ExtensionRegistry reg, ReplicaIndexes idx) Setup(long applied)
    {
        var idx = new ReplicaIndexes();
        var sets = new ReadWriteSets();
        sets.TableWriteSet.Add("Items");
        sets.ColumnWriteSets["Items"] = new RefCountSet();
        sets.ColumnWriteSets["Items"].Add("*");
        sets.ClassByTable["Items"] = AffectingClass.Tas;
        idx.UpdateIndexes(sets, 5);

        var reg = new ExtensionRegistry();
        reg.Register(new ExtensionInfo { Id = "ext1", ConnectionString = "ext1", AppliedTsId = applied, IsOnline = true });
        var router = new QueryRouter(idx, reg, () => 5L, new HihooiMetrics());
        return (router, reg, idx);
    }

    private static ReadWriteSets ReadItems()
    {
        var r = new ReadWriteSets();
        r.TableReadSet.Add("Items");
        r.ColumnReadSets["Items"] = new RefCountSet();
        r.ColumnReadSets["Items"].Add("Name");
        r.ClassByTable["Items"] = AffectingClass.Cas;
        return r;
    }

    [Fact]
    public void Gssi_falls_back_to_primary_when_replica_lags()
    {
        var (router, _, _) = Setup(applied: 0);
        var d = router.RouteRead(ReadItems(), ConsistencyLevel.Gssi);
        Assert.Equal(RouteTargetKind.Primary, d.Target);
    }

    [Fact]
    public void Gssi_uses_extension_when_caught_up()
    {
        var (router, _, _) = Setup(applied: 5);
        var d = router.RouteRead(ReadItems(), ConsistencyLevel.Gssi);
        Assert.Equal(RouteTargetKind.Extension, d.Target);
        Assert.Equal("ext1", d.ExtensionId);
    }

    [Fact]
    public void WeakSi_uses_extension_even_when_lagging()
    {
        var (router, _, _) = Setup(applied: 0);
        var d = router.RouteRead(ReadItems(), ConsistencyLevel.WeakSi);
        Assert.Equal(RouteTargetKind.Extension, d.Target);
    }

    [Fact]
    public void RsiPc_times_out_to_primary_when_never_caught_up()
    {
        var (router, _, _) = Setup(applied: 0);
        var d = router.RouteRead(ReadItems(), ConsistencyLevel.RsiPc, TimeSpan.FromMilliseconds(30));
        Assert.Equal(RouteTargetKind.Primary, d.Target);
        Assert.Contains("fallback", d.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Eligible_replicas_are_chosen_round_robin()
    {
        var idx = new ReplicaIndexes();
        var reg = new ExtensionRegistry();
        reg.Register(new ExtensionInfo { Id = "ext1", ConnectionString = "ext1", AppliedTsId = 5, IsOnline = true });
        reg.Register(new ExtensionInfo { Id = "ext2", ConnectionString = "ext2", AppliedTsId = 5, IsOnline = true });
        var router = new QueryRouter(idx, reg, () => 5L, new HihooiMetrics());
        var a = router.RouteRead(ReadItems(), ConsistencyLevel.WeakSi);
        var b = router.RouteRead(ReadItems(), ConsistencyLevel.WeakSi);
        Assert.Equal(RouteTargetKind.Extension, a.Target);
        Assert.Equal(RouteTargetKind.Extension, b.Target);
        Assert.NotEqual(a.ExtensionId, b.ExtensionId);
        Assert.Contains(a.Candidates, c => c.Selected);
        Assert.Contains(b.Candidates, c => !c.Selected && c.RejectReason == "round-robin-waiting-turn");
    }

    [Fact]
    public void OneSr_routes_to_any_extension()
    {
        var (router, _, _) = Setup(applied: 0);
        var d = router.RouteRead(ReadItems(), ConsistencyLevel.OneCopySerializability);
        Assert.Equal(RouteTargetKind.Extension, d.Target);
    }
}
