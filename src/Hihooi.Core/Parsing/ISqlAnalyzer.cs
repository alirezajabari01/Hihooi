using Hihooi.Core.Model;

namespace Hihooi.Core.Parsing;

public interface ISqlAnalyzer
{
    SqlStatementInfo Analyze(string sql, IReadOnlyDictionary<string, object?>? parameters = null);
}
