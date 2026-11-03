using Hihooi.Core.Model;

namespace Hihooi.Core.Abstractions;

public interface ITransactionsBuffer
{
    ValueTask PublishAsync(TransactionState state, CancellationToken cancellationToken = default);
    IAsyncEnumerable<TransactionState> SubscribeAsync(string consumerId, CancellationToken cancellationToken = default);
    ValueTask AcknowledgeAppliedAsync(string consumerId, long contiguousTsId, CancellationToken cancellationToken = default);

    /// <summary>Transaction-log entries with TSID greater than <paramref name="exclusiveTsId"/>, oldest first.</summary>
    ValueTask<IReadOnlyList<ReplicationLogEntry>> ReadLogAfterAsync(
        long exclusiveTsId,
        int take = 50,
        CancellationToken cancellationToken = default);
}
