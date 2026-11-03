using System.Collections.Concurrent;
using System.Reflection;

namespace Hihooi.Core.Mapping;

/// <summary>
/// Maps dictionary rows from the provider to POCOs without an ORM dependency.
/// </summary>
public static class RowMapper
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> Props = new();

    public static T Map<T>(IReadOnlyDictionary<string, object?> row)
    {
        var type = typeof(T);
        if (type == typeof(IReadOnlyDictionary<string, object?>) || type == typeof(Dictionary<string, object?>))
            return (T)(object)new Dictionary<string, object?>(row, StringComparer.OrdinalIgnoreCase);

        if (IsSimple(type))
        {
            if (row.Count == 0)
                return default!;
            var first = row.Values.FirstOrDefault();
            return (T)ConvertTo(first, type)!;
        }

        var instance = Activator.CreateInstance<T>();
        var props = Props.GetOrAdd(type, static t =>
            t.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(p => p.CanWrite && p.GetIndexParameters().Length == 0)
                .ToArray());

        foreach (var prop in props)
        {
            if (!TryGetValue(row, prop.Name, out var raw))
                continue;
            prop.SetValue(instance, ConvertTo(raw, prop.PropertyType));
        }

        return instance;
    }

    public static IReadOnlyList<T> MapAll<T>(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        if (rows.Count == 0)
            return Array.Empty<T>();
        var list = new List<T>(rows.Count);
        foreach (var row in rows)
            list.Add(Map<T>(row));
        return list;
    }

    private static bool TryGetValue(IReadOnlyDictionary<string, object?> row, string name, out object? value)
    {
        if (row.TryGetValue(name, out value))
            return true;
        foreach (var kv in row)
        {
            if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = kv.Value;
                return true;
            }
        }
        value = null;
        return false;
    }

    private static bool IsSimple(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsPrimitive
               || type.IsEnum
               || type == typeof(string)
               || type == typeof(decimal)
               || type == typeof(DateTime)
               || type == typeof(DateTimeOffset)
               || type == typeof(Guid)
               || type == typeof(byte[]);
    }

    private static object? ConvertTo(object? raw, Type target)
    {
        if (raw is null || raw is DBNull)
        {
            if (target.IsValueType && Nullable.GetUnderlyingType(target) is null)
                return Activator.CreateInstance(target);
            return null;
        }

        var underlying = Nullable.GetUnderlyingType(target) ?? target;
        if (underlying.IsInstanceOfType(raw))
            return raw;

        if (underlying.IsEnum)
        {
            if (raw is string s)
                return Enum.Parse(underlying, s, ignoreCase: true);
            return Enum.ToObject(underlying, raw);
        }

        if (underlying == typeof(Guid))
            return raw is Guid g ? g : Guid.Parse(Convert.ToString(raw)!);

        if (underlying == typeof(DateTimeOffset))
        {
            if (raw is DateTimeOffset dto) return dto;
            if (raw is DateTime dt) return new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
            return DateTimeOffset.Parse(Convert.ToString(raw)!);
        }

        if (underlying == typeof(byte[]) && raw is byte[] bytes)
            return bytes;

        return Convert.ChangeType(raw, underlying);
    }
}
