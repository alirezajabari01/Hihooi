using Hihooi.Core.Model;
using Hihooi.Core.Parsing;

namespace Hihooi.Tests.Parsing;

public class LightweightSqlAnalyzerTests
{
    private readonly LightweightSqlAnalyzer _sut = new();

    [Fact]
    public void Update_with_pk_equality_is_ras()
    {
        var info = _sut.Analyze("UPDATE R SET A2 = 1, A3 = 2 WHERE A1 = 100");
        Assert.True(info.IsWrite);
        Assert.Equal(AffectingClass.Ras, info.Sets.ClassByTable["R"]);
        Assert.Contains("A2", info.Sets.ColumnWriteSets["R"].Counts.Keys);
        Assert.Contains("A1=100", info.Sets.RowWriteSets["R"].Counts.Keys);
    }

    [Fact]
    public void Update_range_predicate_is_cas()
    {
        var info = _sut.Analyze("UPDATE S SET B2 = 1 WHERE B5 > 10");
        Assert.Equal(AffectingClass.Cas, info.Sets.ClassByTable["S"]);
    }

    [Fact]
    public void Select_star_is_tas()
    {
        var info = _sut.Analyze("SELECT * FROM R WHERE A2 > 1");
        Assert.False(info.IsWrite);
        Assert.Equal(AffectingClass.Tas, info.Sets.ClassByTable["R"]);
    }

    [Fact]
    public void Delete_with_pk_is_ras()
    {
        var info = _sut.Analyze("DELETE FROM R WHERE A1 = 120");
        Assert.Equal(AffectingClass.Ras, info.Sets.ClassByTable["R"]);
    }

    [Fact]
    public void Ddl_is_tas_write()
    {
        var info = _sut.Analyze("CREATE TABLE Foo (Id INT)");
        Assert.True(info.IsWrite);
        Assert.Equal(AffectingClass.Tas, info.Sets.ClassByTable["Foo"]);
    }

    [Fact]
    public void Rewrites_sysdatetime()
    {
        var info = _sut.Analyze("UPDATE R SET A2 = SYSDATETIME() WHERE A1 = 1");
        Assert.NotNull(info.RewrittenSql);
        Assert.DoesNotContain("SYSDATETIME", info.RewrittenSql, StringComparison.OrdinalIgnoreCase);
    }
}
