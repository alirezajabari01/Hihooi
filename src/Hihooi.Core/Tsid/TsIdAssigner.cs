using Hihooi.Core.Model;

namespace Hihooi.Core.Tsid;

/// <summary>
/// Assigns contiguous TSIDs in Primary commit-timestamp order (paper §4.2).
/// Pending completions are buffered and flushed in timestamp order to handle
/// out-of-order commit responses.
/// </summary>
public sealed class TsIdAssigner
{
    private readonly object _gate = new();
    private long _nextTsId = 1;
    private readonly List<Pending> _pending = new();
    private int _inflightCommits;

    private sealed class Pending
    {
        public required TransactionState State { get; init; }
        public required TaskCompletionSource<long> Tcs { get; init; }
    }

    public void BeginCommitAttempt()
    {
        lock (_gate) _inflightCommits++;
    }

    public Task<long> CompleteAsync(TransactionState state)
    {
        var tcs = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _pending.Add(new Pending { State = state, Tcs = tcs });
            _inflightCommits = Math.Max(0, _inflightCommits - 1);
            TryFlushUnlocked();
        }
        return tcs.Task;
    }

    /// <summary>
    /// When no other commits are in-flight, assign TSIDs sorted by CommitTimestampUtc.
    /// If commits are still in-flight, wait until they arrive so ordering matches paper.
    /// A short grace is applied by only flushing when inflight is 0.
    /// </summary>
    private void TryFlushUnlocked()
    {
        if (_inflightCommits > 0) return;

        _pending.Sort((a, b) =>
        {
            var cmp = a.State.CommitTimestampUtc.CompareTo(b.State.CommitTimestampUtc);
            if (cmp != 0) return cmp;
            return a.State.TransactionId.CompareTo(b.State.TransactionId);
        });

        foreach (var p in _pending)
        {
            var id = _nextTsId++;
            p.State.TsId = id;
            p.Tcs.TrySetResult(id);
        }
        _pending.Clear();
    }

    public long PeekNext() { lock (_gate) return _nextTsId; }

    public long LatestAssigned { get { lock (_gate) return _nextTsId - 1; } }
}
