namespace Hihooi.Core.Model;

public sealed class SqlStatementInfo
{
    public required string Sql { get; init; }
    public IReadOnlyDictionary<string, object?> Parameters { get; init; } =
        new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    public ReadWriteSets Sets { get; init; } = new();
    public bool IsWrite { get; init; }
    public string? RewrittenSql { get; set; }
}
