using Hihooi.Core.Indexes;
using Hihooi.Core.Model;
using Hihooi.Core.Parsing;

namespace Hihooi.Tests.Algorithms;

public class ReplicaIndexesStarTests
{
    [Fact]
    public void Tas_insert_star_blocks_cas_read_until_tsid()
    {
        var analyzer = new LightweightSqlAnalyzer();
        var insert = analyzer.Analyze("INSERT INTO R (Id, Name) VALUES (1, 'a')");
        Assert.Equal(AffectingClass.Tas, insert.Sets.ClassByTable["R"]);
        Assert.Contains("*", insert.Sets.ColumnWriteSets["R"].Counts.Keys);

        var idx = new ReplicaIndexes();
        idx.UpdateIndexes(insert.Sets, 7);

        var read = analyzer.Analyze("SELECT Name FROM R WHERE Id = 1");
        Assert.Equal(7, idx.FindLatestConsistentTsId(read.Sets));
    }
}
