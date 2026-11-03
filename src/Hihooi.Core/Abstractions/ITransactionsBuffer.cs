using Hihooi.Core.Model;

namespace Hihooi.Core.Abstractions;

public interface ITransactionsBuffer
{
    ValueTask PublishAsync(TransactionState state, CancellationToken cancellationToken = default);
    IAsyncEnumerable<TransactionState> SubscribeAsync(string consumerId, CancellationToken cancellationToken = default);
    ValueTask AcknowledgeAppliedAsync(string consumerId, long contiguousTsId, CancellationToken cancellationToken = default);
}
