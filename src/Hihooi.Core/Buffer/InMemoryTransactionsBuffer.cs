using System.Threading.Channels;
using Hihooi.Core.Abstractions;
using Hihooi.Core.Model;

namespace Hihooi.Core.Buffer;

/// <summary>
/// In-process Transactions Buffer (adaptation replacing Memcached for library use).
/// Preserves publish order; each consumer gets all TStates from subscribe point / backlog.
/// </summary>
public sealed class InMemoryTransactionsBuffer : ITransactionsBuffer
{
    private readonly object _gate = new();
    private readonly List<TransactionState> _log = new();
    private readonly Dictionary<string, Channel<TransactionState>> _consumers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _consumerContiguous = new(StringComparer.OrdinalIgnoreCase);

    public ValueTask PublishAsync(TransactionState state, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _log.Add(state);
            foreach (var ch in _consumers.Values)
                ch.Writer.TryWrite(state);
        }
        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<TransactionState> SubscribeAsync(
        string consumerId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Channel<TransactionState> channel;
        List<TransactionState> backlog;
        lock (_gate)
        {
            channel = Channel.CreateUnbounded<TransactionState>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });
            _consumers[consumerId] = channel;
            var from = _consumerContiguous.TryGetValue(consumerId, out var c) ? c : 0;
            backlog = _log.Where(s => s.TsId > from).ToList();
        }

        foreach (var s in backlog)
            yield return s;

        while (await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (channel.Reader.TryRead(out var item))
                yield return item;
        }
    }

    public ValueTask AcknowledgeAppliedAsync(string consumerId, long contiguousTsId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _consumerContiguous[consumerId] = contiguousTsId;
        }
        return ValueTask.CompletedTask;
    }

    public long MinContiguousAppliedAcrossConsumers()
    {
        lock (_gate)
        {
            if (_consumerContiguous.Count == 0) return 0;
            return _consumerContiguous.Values.Min();
        }
    }

    public IReadOnlyList<TransactionState> SnapshotLog()
    {
        lock (_gate) return _log.ToList();
    }
}
