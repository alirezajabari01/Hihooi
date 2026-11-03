using System.Collections;
using System.Reflection;

namespace Hihooi.Core.Parsing;

/// <summary>
/// Converts public API parameter objects into named SQL parameter dictionaries.
/// Supports anonymous objects, dictionaries, and positional lists (@p0, @p1, …).
/// </summary>
public static class SqlParameterBinder
{
    private static readonly IReadOnlyDictionary<string, object?> Empty =
        new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyDictionary<string, object?> Bind(object? parameters)
    {
        if (parameters is null)
            return Empty;

        if (parameters is IReadOnlyDictionary<string, object?> typed)
            return Normalize(typed);

        if (parameters is IDictionary<string, object?> dict)
            return Normalize(dict);

        if (parameters is IDictionary nonGeneric)
        {
            var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry entry in nonGeneric)
            {
                if (entry.Key is null) continue;
                map[NormalizeName(entry.Key.ToString()!)] = entry.Value;
            }
            return map;
        }

        if (parameters is IReadOnlyList<object?> list)
        {
            var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < list.Count; i++)
                map["p" + i] = list[i];
            return map;
        }

        if (parameters is IEnumerable enumerable and not string)
        {
            // Avoid treating string as char enumerable; treat other sequences as positional when not a dictionary.
            var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            var i = 0;
            foreach (var item in enumerable)
            {
                map["p" + i] = item;
                i++;
            }
            if (i > 0)
                return map;
        }

        var props = parameters.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public);
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in props)
        {
            if (!prop.CanRead || prop.GetIndexParameters().Length > 0)
                continue;
            result[prop.Name] = prop.GetValue(parameters);
        }
        return result;
    }

    private static IReadOnlyDictionary<string, object?> Normalize(IEnumerable<KeyValuePair<string, object?>> source)
    {
        var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in source)
            map[NormalizeName(kv.Key)] = kv.Value;
        return map;
    }

    /// <summary>Strip leading @; SqlClient parameter names are stored without requiring a leading @ in the dictionary key.</summary>
    public static string NormalizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Parameter name cannot be empty.", nameof(name));
        return name.Trim().TrimStart('@');
    }

    public static string ToSqlParameterName(string name)
        => "@" + NormalizeName(name);
}
