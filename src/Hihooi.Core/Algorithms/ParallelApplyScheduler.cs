using Hihooi.Core.Model;

namespace Hihooi.Core.Algorithms;

/// <summary>
/// Algorithm 2 — parallel transaction execution at Extension DBs (paper §4.2).
/// Thread-safe for a single Extractor instance.
/// </summary>
public sealed class ParallelApplyScheduler
{
    private readonly object _gate = new();
    private readonly ReadWriteSets _runningState = new();
    private readonly ReadWriteSets _waitingState = new();
    private readonly Queue<TransactionState> _waitQueue = new();
    private readonly HashSet<Guid> _runningIds = new();

    public IReadOnlyCollection<Guid> RunningIds
    {
        get { lock (_gate) return _runningIds.ToArray(); }
    }

    /// <summary>
    /// Returns transactions that may start execution immediately (possibly empty if queued).
    /// </summary>
    public IReadOnlyList<TransactionState> OnNewTransaction(TransactionState tsNew)
    {
        lock (_gate)
        {
            if (IndependenceAnalyzer.AreIndependent(_runningState, tsNew.Sets)
                && IndependenceAnalyzer.AreIndependent(_waitingState, tsNew.Sets))
            {
                _runningState.Merge(tsNew.Sets);
                _runningIds.Add(tsNew.TransactionId);
                return new[] { tsNew };
            }

            _waitingState.Merge(tsNew.Sets);
            _waitQueue.Enqueue(tsNew);
            return Array.Empty<TransactionState>();
        }
    }

    /// <summary>
    /// Called when a running transaction completes; may release waiting transactions.
    /// </summary>
    public IReadOnlyList<TransactionState> OnTransactionComplete(TransactionState tsOld)
    {
        lock (_gate)
        {
            _runningState.Remove(tsOld.Sets);
            _runningIds.Remove(tsOld.TransactionId);

            var toRun = new List<TransactionState>();
            while (_waitQueue.Count > 0
                   && IndependenceAnalyzer.AreIndependent(_runningState, _waitQueue.Peek().Sets))
            {
                var tsRun = _waitQueue.Dequeue();
                _waitingState.Remove(tsRun.Sets);
                _runningState.Merge(tsRun.Sets);
                _runningIds.Add(tsRun.TransactionId);
                toRun.Add(tsRun);
            }

            return toRun;
        }
    }
}
