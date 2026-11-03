namespace Hihooi.Core.Model;

/// <summary>
/// Hash-map based set with reference counters for merge/remove (paper §4.2).
/// </summary>
public sealed class RefCountSet
{
    private readonly Dictionary<string, int> _counts = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, int> Counts => _counts;

    public bool IsEmpty => _counts.Count == 0;

    public void Add(string item)
    {
        if (string.IsNullOrWhiteSpace(item)) return;
        _counts[item] = _counts.TryGetValue(item, out var c) ? c + 1 : 1;
    }

    public void AddRange(IEnumerable<string> items)
    {
        foreach (var item in items) Add(item);
    }

    public void Merge(RefCountSet other)
    {
        foreach (var (key, value) in other._counts)
            _counts[key] = _counts.TryGetValue(key, out var c) ? c + value : value;
    }

    public void Remove(RefCountSet other)
    {
        foreach (var (key, value) in other._counts)
        {
            if (!_counts.TryGetValue(key, out var c)) continue;
            var next = c - value;
            if (next <= 0) _counts.Remove(key);
            else _counts[key] = next;
        }
    }

    public bool Intersects(RefCountSet other)
    {
        if (_counts.Count == 0 || other._counts.Count == 0) return false;
        foreach (var key in _counts.Keys)
        {
            if (other._counts.ContainsKey(key)) return true;
        }
        return false;
    }

    public HashSet<string> ToHashSet() => new(_counts.Keys, StringComparer.OrdinalIgnoreCase);

    public RefCountSet Clone()
    {
        var clone = new RefCountSet();
        foreach (var (k, v) in _counts) clone._counts[k] = v;
        return clone;
    }

    public void Clear() => _counts.Clear();
}
