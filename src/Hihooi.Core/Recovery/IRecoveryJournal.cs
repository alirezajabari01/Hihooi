using Hihooi.Core.Model;

namespace Hihooi.Core.Recovery;

public enum JournalEntryStatus
{
    PendingPublish = 0,
    Published = 1,
    AppliedEverywhere = 2,
    Failed = 3
}

public sealed class JournalEntry
{
    public required long TsId { get; init; }
    public required Guid TransactionId { get; init; }
    public required CompletionOperation Completion { get; init; }
    public required JournalEntryStatus Status { get; set; }
    public required string PayloadJson { get; init; }
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

public interface IRecoveryJournal
{
    Task AppendAsync(JournalEntry entry, CancellationToken cancellationToken = default);
    Task UpdateStatusAsync(long tsId, JournalEntryStatus status, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<JournalEntry>> GetPendingAsync(CancellationToken cancellationToken = default);
}
