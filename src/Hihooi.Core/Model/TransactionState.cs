namespace Hihooi.Core.Model;

/// <summary>TState as defined in Hihooi §4.2.</summary>
public sealed class TransactionState
{
    public long TsId { get; set; }
    public Guid TransactionId { get; init; } = Guid.NewGuid();
    public List<SqlStatementInfo> WriteStatements { get; } = new();
    public ReadWriteSets Sets { get; } = new();
    public TimeSpan PrimaryExecutionTime { get; set; }
    public CompletionOperation Completion { get; set; } = CompletionOperation.Commit;
    public DateTime CommitTimestampUtc { get; set; }
    public bool IsFailed { get; set; }

    public void MergeStatement(SqlStatementInfo statement)
    {
        if (statement.IsWrite)
            WriteStatements.Add(statement);
        Sets.Merge(statement.Sets);
    }

    public TransactionState CloneForMergeTracking()
    {
        var clone = new TransactionState
        {
            TsId = TsId,
            TransactionId = TransactionId,
            PrimaryExecutionTime = PrimaryExecutionTime,
            Completion = Completion,
            CommitTimestampUtc = CommitTimestampUtc,
            IsFailed = IsFailed
        };
        clone.Sets.Merge(Sets);
        foreach (var s in WriteStatements) clone.WriteStatements.Add(s);
        return clone;
    }
}
