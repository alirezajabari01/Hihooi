namespace Hihooi.Core.Tsid;

/// <summary>
/// Tracks applied TSIDs per Extension and reports the latest contiguous (gap-free) value.
/// Paper §4.2: when W1,W3,W2 complete out of order, report only contiguous prefixes.
/// </summary>
public sealed class ContiguousTsIdTracker
{
    private readonly object _gate = new();
    private readonly SortedSet<long> _completed = new();
    private long _contiguous;

    public long ContiguousApplied
    {
        get { lock (_gate) return _contiguous; }
    }

    public long ReportCompleted(long tsId)
    {
        lock (_gate)
        {
            if (tsId <= 0) return _contiguous;
            _completed.Add(tsId);
            while (_completed.Contains(_contiguous + 1))
            {
                _contiguous++;
                _completed.Remove(_contiguous);
            }
            return _contiguous;
        }
    }

    public void Reset(long contiguous = 0)
    {
        lock (_gate)
        {
            _contiguous = contiguous;
            _completed.Clear();
        }
    }
}
