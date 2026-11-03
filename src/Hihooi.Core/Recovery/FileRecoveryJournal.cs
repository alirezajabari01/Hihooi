using System.Text.Json;
using Hihooi.Core.Model;

namespace Hihooi.Core.Recovery;

/// <summary>
/// Append-only JSONL recovery journal for restart replay of published TStates.
/// </summary>
public sealed class FileRecoveryJournal : IRecoveryJournal
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    public FileRecoveryJournal(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "hihooi-journal.jsonl");
    }

    public async Task AppendAsync(JournalEntry entry, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var line = JsonSerializer.Serialize(entry, JsonOpts);
            await File.AppendAllTextAsync(_path, line + Environment.NewLine, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task UpdateStatusAsync(long tsId, JournalEntryStatus status, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path)) return;
            var lines = await File.ReadAllLinesAsync(_path, cancellationToken).ConfigureAwait(false);
            var rewritten = new List<string>(lines.Length);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JournalEntry? e = null;
                try { e = JsonSerializer.Deserialize<JournalEntry>(line, JsonOpts); }
                catch (JsonException) { continue; }
                if (e is not null && e.TsId == tsId)
                {
                    e.Status = status;
                    e.UpdatedUtc = DateTime.UtcNow;
                    rewritten.Add(JsonSerializer.Serialize(e, JsonOpts));
                }
                else rewritten.Add(line);
            }
            await File.WriteAllLinesAsync(_path, rewritten, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<JournalEntry>> GetPendingAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path)) return Array.Empty<JournalEntry>();
            var lines = await File.ReadAllLinesAsync(_path, cancellationToken).ConfigureAwait(false);
            var list = new List<JournalEntry>();
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JournalEntry? e;
                try
                {
                    e = JsonSerializer.Deserialize<JournalEntry>(line, JsonOpts);
                }
                catch (JsonException)
                {
                    // Truncated / concurrent-write corrupt line — skip so StartAsync can proceed.
                    continue;
                }
                if (e is not null && e.Status is JournalEntryStatus.PendingPublish or JournalEntryStatus.Published)
                    list.Add(e);
            }
            return list.OrderBy(e => e.TsId).ToList();
        }
        finally { _gate.Release(); }
    }

    public static string SerializeState(TransactionState state)
        => JsonSerializer.Serialize(new TStateDto(state), JsonOpts);

    public static TransactionState? DeserializeState(string json)
    {
        var dto = JsonSerializer.Deserialize<TStateDto>(json, JsonOpts);
        return dto?.ToState();
    }

    private sealed class TStateDto
    {
        public long TsId { get; set; }
        public Guid TransactionId { get; set; }
        public CompletionOperation Completion { get; set; }
        public bool IsFailed { get; set; }
        public DateTime CommitTimestampUtc { get; set; }
        public List<StmtDto> Statements { get; set; } = new();

        public TStateDto() { }
        public TStateDto(TransactionState s)
        {
            TsId = s.TsId;
            TransactionId = s.TransactionId;
            Completion = s.Completion;
            IsFailed = s.IsFailed;
            CommitTimestampUtc = s.CommitTimestampUtc;
            foreach (var w in s.WriteStatements)
            {
                Statements.Add(new StmtDto
                {
                    Sql = w.RewrittenSql ?? w.Sql,
                    Parameters = w.Parameters.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase)
                });
            }
        }

        public TransactionState ToState()
        {
            var s = new TransactionState
            {
                TsId = TsId,
                TransactionId = TransactionId,
                Completion = Completion,
                IsFailed = IsFailed,
                CommitTimestampUtc = CommitTimestampUtc
            };
            foreach (var st in Statements)
            {
                s.WriteStatements.Add(new SqlStatementInfo
                {
                    Sql = st.Sql,
                    RewrittenSql = st.Sql,
                    Parameters = st.Parameters ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase),
                    IsWrite = true,
                    Sets = new ReadWriteSets()
                });
            }
            return s;
        }
    }

    private sealed class StmtDto
    {
        public string Sql { get; set; } = "";
        public Dictionary<string, object?> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
