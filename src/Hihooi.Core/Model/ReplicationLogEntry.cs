namespace Hihooi.Core.Model;

/// <summary>One committed transaction still sitting in the Hihooi transaction log.</summary>
public sealed class ReplicationLogEntry
{
    public long TsId { get; init; }
    public string Sql { get; init; } = "";
    public DateTime CommitTimestampUtc { get; init; }
    public string Completion { get; init; } = "";
    public bool IsFailed { get; init; }

    public static ReplicationLogEntry From(TransactionState state) => new()
    {
        TsId = state.TsId,
        Sql = string.Join("\n", state.WriteStatements.Select(w => w.RewrittenSql ?? w.Sql)),
        CommitTimestampUtc = state.CommitTimestampUtc,
        Completion = state.Completion.ToString(),
        IsFailed = state.IsFailed
    };
}
