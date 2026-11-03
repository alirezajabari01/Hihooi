using System.Runtime.CompilerServices;
using System.Text.Json;
using Hihooi.Core.Abstractions;
using Hihooi.Core.Model;
using StackExchange.Redis;

namespace Hihooi.Redis;

/// <summary>
/// Distributed Transactions Buffer using Redis:
/// - ZSET of TSID members for total order
/// - HASH payload by TSID (avoids duplicate members when JSON differs)
/// Cursor per consumer; TTL on keys. No multi-TM fencing.
/// </summary>
public sealed class RedisTransactionsBuffer : ITransactionsBuffer, IAsyncDisposable
{
    private readonly IConnectionMultiplexer _mux;
    private readonly string _prefix;
    private readonly TimeSpan _ttl;
    private static readonly JsonSerializerOptions JsonOpts = new();

    public RedisTransactionsBuffer(IConnectionMultiplexer mux, string keyPrefix = "hihooi:", TimeSpan? ttl = null)
    {
        _mux = mux ?? throw new ArgumentNullException(nameof(mux));
        _prefix = keyPrefix;
        _ttl = ttl ?? TimeSpan.FromDays(7);
    }

    private string LogKey => _prefix + "tstates";
    private string PayloadKey => _prefix + "payloads";
    private string CursorKey(string consumerId) => _prefix + "cursor:" + consumerId;

    public async ValueTask PublishAsync(TransactionState state, CancellationToken cancellationToken = default)
    {
        try
        {
            var db = _mux.GetDatabase();
            var payload = JsonSerializer.Serialize(ToDto(state), JsonOpts);
            var member = state.TsId.ToString();
            // Idempotent by TSID: overwrite payload, ensure single ZSET member
            await db.HashSetAsync(PayloadKey, member, payload).ConfigureAwait(false);
            await db.SortedSetAddAsync(LogKey, member, state.TsId).ConfigureAwait(false);
            await db.KeyExpireAsync(LogKey, _ttl).ConfigureAwait(false);
            await db.KeyExpireAsync(PayloadKey, _ttl).ConfigureAwait(false);
        }
        catch (RedisException ex)
        {
            throw new InvalidOperationException($"Hihooi Redis publish failed for TSID {state.TsId}: {ex.Message}", ex);
        }
    }

    public async IAsyncEnumerable<TransactionState> SubscribeAsync(
        string consumerId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        long last;
        try
        {
            var db = _mux.GetDatabase();
            last = (long)await db.StringGetAsync(CursorKey(consumerId)).ConfigureAwait(false);
        }
        catch (RedisException ex)
        {
            throw new InvalidOperationException($"Hihooi Redis subscribe init failed: {ex.Message}", ex);
        }

        await foreach (var state in ReplayFromAsync(last, cancellationToken).ConfigureAwait(false))
            yield return state;

        while (!cancellationToken.IsCancellationRequested)
        {
            IEnumerable<TransactionState> batch;
            try
            {
                var db = _mux.GetDatabase();
                last = (long)await db.StringGetAsync(CursorKey(consumerId)).ConfigureAwait(false);
                batch = await ReadBatchAsync(db, last).ConfigureAwait(false);
            }
            catch (RedisConnectionException) when (!cancellationToken.IsCancellationRequested)
            {
                try { await Task.Delay(500, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { yield break; }
                continue;
            }
            catch (RedisException) when (!cancellationToken.IsCancellationRequested)
            {
                try { await Task.Delay(500, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { yield break; }
                continue;
            }

            var any = false;
            foreach (var state in batch)
            {
                any = true;
                yield return state;
            }

            if (!any)
            {
                try { await Task.Delay(50, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { yield break; }
            }
        }
    }

    private async IAsyncEnumerable<TransactionState> ReplayFromAsync(
        long last,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var db = _mux.GetDatabase();
        foreach (var state in await ReadBatchAsync(db, last).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return state;
        }
    }

    private async Task<List<TransactionState>> ReadBatchAsync(IDatabase db, long last)
    {
        var ids = await db.SortedSetRangeByScoreAsync(LogKey, last + 1, double.PositiveInfinity, take: 50)
            .ConfigureAwait(false);
        var list = new List<TransactionState>(ids.Length);
        foreach (var id in ids)
        {
            var payload = await db.HashGetAsync(PayloadKey, id!).ConfigureAwait(false);
            if (payload.IsNullOrEmpty) continue;
            var dto = JsonSerializer.Deserialize<TStateDto>((string)payload!, JsonOpts);
            if (dto is not null) list.Add(FromDto(dto));
        }
        return list;
    }

    public async ValueTask AcknowledgeAppliedAsync(string consumerId, long contiguousTsId, CancellationToken cancellationToken = default)
    {
        try
        {
            var db = _mux.GetDatabase();
            await db.StringSetAsync(CursorKey(consumerId), contiguousTsId).ConfigureAwait(false);
            await db.KeyExpireAsync(CursorKey(consumerId), _ttl).ConfigureAwait(false);
        }
        catch (RedisException ex)
        {
            throw new InvalidOperationException($"Hihooi Redis ack failed for {consumerId}: {ex.Message}", ex);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static TStateDto ToDto(TransactionState s) => new()
    {
        TsId = s.TsId,
        TransactionId = s.TransactionId,
        Completion = (int)s.Completion,
        IsFailed = s.IsFailed,
        CommitTimestampUtc = s.CommitTimestampUtc,
        Statements = s.WriteStatements.Select(w => new StmtDto
        {
            Sql = w.RewrittenSql ?? w.Sql,
            Parameters = w.Parameters.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase)
        }).ToList()
    };

    private static TransactionState FromDto(TStateDto d)
    {
        var s = new TransactionState
        {
            TsId = d.TsId,
            TransactionId = d.TransactionId,
            Completion = (CompletionOperation)d.Completion,
            IsFailed = d.IsFailed,
            CommitTimestampUtc = d.CommitTimestampUtc
        };
        foreach (var st in d.Statements)
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

    private sealed class TStateDto
    {
        public long TsId { get; set; }
        public Guid TransactionId { get; set; }
        public int Completion { get; set; }
        public bool IsFailed { get; set; }
        public DateTime CommitTimestampUtc { get; set; }
        public List<StmtDto> Statements { get; set; } = new();
    }

    private sealed class StmtDto
    {
        public string Sql { get; set; } = "";
        public Dictionary<string, object?> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
