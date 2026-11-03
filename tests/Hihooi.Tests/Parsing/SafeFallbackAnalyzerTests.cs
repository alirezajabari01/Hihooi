using Hihooi.Core.Model;
using Hihooi.Core.Parsing;

namespace Hihooi.Tests.Parsing;

public class SafeFallbackAnalyzerTests
{
    private readonly LightweightSqlAnalyzer _sut = new();

    [Fact]
    public void Join_select_is_tas_safe_fallback()
    {
        var info = _sut.Analyze("SELECT a.Id, b.Name FROM Orders a JOIN Customers b ON a.CustomerId=b.Id");
        Assert.False(info.IsWrite);
        Assert.Contains(info.Sets.ClassByTable.Values, c => c == AffectingClass.Tas);
    }

    [Fact]
    public void Cte_is_safe_fallback()
    {
        var info = _sut.Analyze("WITH x AS (SELECT Id FROM R) SELECT * FROM x");
        Assert.True(info.Sets.ClassByTable.Values.All(c => c == AffectingClass.Tas));
    }

    [Fact]
    public void Insert_select_is_write_tas()
    {
        var info = _sut.Analyze("INSERT INTO R (Id) SELECT Id FROM S");
        Assert.True(info.IsWrite);
        Assert.Equal(AffectingClass.Tas, info.Sets.ClassByTable.Values.First());
    }

    [Fact]
    public void Exec_is_conservative_write()
    {
        var info = _sut.Analyze("EXEC dbo.DoStuff @id=1");
        Assert.True(info.IsWrite);
    }
}
