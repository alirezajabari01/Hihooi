using Hihooi.Core.Tsid;

namespace Hihooi.Tests.Algorithms;

public class ContiguousTsIdTrackerTests
{
    [Fact]
    public void Reports_contiguous_prefix_without_gaps()
    {
        var t = new ContiguousTsIdTracker();
        Assert.Equal(1, t.ReportCompleted(1));
        Assert.Equal(1, t.ReportCompleted(3)); // gap at 2
        Assert.Equal(3, t.ReportCompleted(2)); // fills gap → 1,2,3
        Assert.Equal(3, t.ReportCompleted(5));
        Assert.Equal(5, t.ReportCompleted(4));
    }
}
