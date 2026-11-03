using System.Text.RegularExpressions;

namespace Hihooi.Core.Diagnostics.Tracing;

public static partial class TraceMasking
{
    private static readonly string[] SensitiveNames =
    [
        "password", "pwd", "secret", "token", "apikey", "api_key", "authorization",
        "connectionstring", "connstr", "access_token", "accesstoken", "credential"
    ];

    public static bool IsSensitiveName(string name)
    {
        var n = name.Trim().TrimStart('@');
        return SensitiveNames.Any(s => n.Contains(s, StringComparison.OrdinalIgnoreCase));
    }

    public static Dictionary<string, string?> MaskParameters(
        IReadOnlyDictionary<string, object?>? parameters,
        bool maskSensitive,
        int maxBytes)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (parameters is null) return result;
        var used = 0;
        foreach (var kv in parameters)
        {
            var text = maskSensitive && IsSensitiveName(kv.Key)
                ? "***"
                : Truncate(FormatValue(kv.Value), Math.Max(32, maxBytes / Math.Max(1, parameters.Count)));
            used += (kv.Key.Length + (text?.Length ?? 0));
            if (used > maxBytes) break;
            result[kv.Key] = text;
        }
        return result;
    }

    public static string? Truncate(string? value, int max)
    {
        if (value is null) return null;
        if (value.Length <= max) return value;
        return value[..Math.Max(0, max - 3)] + "...";
    }

    public static string? MaskConnectionString(string? cs)
    {
        if (string.IsNullOrEmpty(cs)) return cs;
        return PasswordRegex().Replace(cs, "$1=***");
    }

    public static (string? Server, string? Database) ParseEndpoint(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return (null, null);
        string? server = null, database = null;
        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var i = part.IndexOf('=');
            if (i <= 0) continue;
            var key = part[..i].Trim();
            var val = part[(i + 1)..].Trim();
            if (key.Equals("Server", StringComparison.OrdinalIgnoreCase)
                || key.Equals("Data Source", StringComparison.OrdinalIgnoreCase)
                || key.Equals("Addr", StringComparison.OrdinalIgnoreCase)
                || key.Equals("Address", StringComparison.OrdinalIgnoreCase))
                server = val;
            else if (key.Equals("Database", StringComparison.OrdinalIgnoreCase)
                     || key.Equals("Initial Catalog", StringComparison.OrdinalIgnoreCase))
                database = val;
        }
        return (server, database);
    }

    private static string FormatValue(object? value) => value switch
    {
        null => "null",
        string s => s,
        byte[] b => $"byte[{b.Length}]",
        _ => Convert.ToString(value) ?? ""
    };

    [GeneratedRegex(@"(Password|Pwd|User\s*Id|UID)\s*=\s*[^;]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PasswordRegex();
}

public static class SqlShapeNormalizer
{
    private static readonly Regex Literals = new(@"'([^']|'')*'|\b\d+(\.\d+)?\b", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    public static string Normalize(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return "";
        var s = Literals.Replace(sql.Trim(), "?");
        s = Whitespace.Replace(s, " ");
        return s.Length > 500 ? s[..500] : s;
    }
}
