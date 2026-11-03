namespace Hihooi.Core.Routing;

public sealed class ExtensionInfo
{
    public required string Id { get; init; }
    public required string ConnectionString { get; init; }
    public long AppliedTsId { get; set; }
    public int InflightReads { get; set; }
    public bool IsOnline { get; set; } = true;
}

/// <summary>Tracks Extension DBs and applied TSIDs; least-loaded selection among eligible.</summary>
public sealed class ExtensionRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ExtensionInfo> _extensions = new(StringComparer.OrdinalIgnoreCase);

    public void Register(ExtensionInfo info)
    {
        lock (_gate) _extensions[info.Id] = info;
    }

    public void Unregister(string id)
    {
        lock (_gate) _extensions.Remove(id);
    }

    public void SetAppliedTsId(string id, long tsId)
    {
        lock (_gate)
        {
            if (_extensions.TryGetValue(id, out var e))
                e.AppliedTsId = tsId;
        }
    }

    public void SetOnline(string id, bool online)
    {
        lock (_gate)
        {
            if (_extensions.TryGetValue(id, out var e))
                e.IsOnline = online;
        }
    }

    public IReadOnlyList<ExtensionInfo> Snapshot()
    {
        lock (_gate) return _extensions.Values.Select(Clone).ToList();
    }

    public ExtensionInfo? SelectLeastLoaded(Func<ExtensionInfo, bool> predicate)
    {
        lock (_gate)
        {
            return _extensions.Values
                .Where(e => e.IsOnline && predicate(e))
                .OrderBy(e => e.InflightReads)
                .ThenBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
                .Select(Clone)
                .FirstOrDefault();
        }
    }

    public IDisposable LeaseRead(string id)
    {
        lock (_gate)
        {
            if (_extensions.TryGetValue(id, out var e))
                e.InflightReads++;
        }
        return new Lease(this, id);
    }

    private void Release(string id)
    {
        lock (_gate)
        {
            if (_extensions.TryGetValue(id, out var e) && e.InflightReads > 0)
                e.InflightReads--;
        }
    }

    public long MaxAppliedTsId()
    {
        lock (_gate)
        {
            if (_extensions.Count == 0) return 0;
            return _extensions.Values.Where(e => e.IsOnline).Select(e => e.AppliedTsId).DefaultIfEmpty(0).Max();
        }
    }

    public long MinAppliedTsId()
    {
        lock (_gate)
        {
            var online = _extensions.Values.Where(e => e.IsOnline).ToList();
            if (online.Count == 0) return 0;
            return online.Min(e => e.AppliedTsId);
        }
    }

    private static ExtensionInfo Clone(ExtensionInfo e) => new()
    {
        Id = e.Id,
        ConnectionString = e.ConnectionString,
        AppliedTsId = e.AppliedTsId,
        InflightReads = e.InflightReads,
        IsOnline = e.IsOnline
    };

    private sealed class Lease : IDisposable
    {
        private readonly ExtensionRegistry _reg;
        private readonly string _id;
        private int _disposed;
        public Lease(ExtensionRegistry reg, string id) { _reg = reg; _id = id; }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                _reg.Release(_id);
        }
    }
}
