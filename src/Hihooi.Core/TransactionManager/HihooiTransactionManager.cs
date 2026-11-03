using System.Text.Json;
using Hihooi.Core.Abstractions;
using Hihooi.Core.Buffer;
using Hihooi.Core.Configuration;
using Hihooi.Core.Consistency;
using Hihooi.Core.Diagnostics;
using Hihooi.Core.Indexes;
using Hihooi.Core.Model;
using Hihooi.Core.Parsing;
using Hihooi.Core.Recovery;
using Hihooi.Core.Replication;
using Hihooi.Core.Routing;
using Hihooi.Core.Tsid;
using Microsoft.Extensions.Logging;

namespace Hihooi.Core.TransactionManager;

public sealed class HihooiTransactionManager : IAsyncDisposable
{
    private readonly IDatabaseProvider _provider;
    private readonly ISqlAnalyzer _analyzer;
    private readonly ITransactionsBuffer _buffer;
    private readonly ReplicaIndexes _indexes = new();
    private readonly ExtensionRegistry _extensions = new();
    private readonly TsIdAssigner _tsIdAssigner = new();
    private readonly List<ExtractorService> _extractors = new();
    private readonly ILogger? _logger;
    private readonly IRecoveryJournal? _journal;
    private readonly IAppliedTsIdStore? _appliedStore;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly object _extractorGate = new();
    private bool _started;

    public HihooiTransactionManager(
        HihooiOptions options,
        IDatabaseProvider provider,
        ISqlAnalyzer? analyzer = null,
        ITransactionsBuffer? buffer = null,
        ILogger<HihooiTransactionManager>? logger = null,
        IRecoveryJournal? journal = null,
        HihooiMetrics? metrics = null,
        IAppliedTsIdStore? appliedStore = null,
        HihooiEventStore? events = null)
    {
        Options = options;
        _provider = provider;
        _analyzer = analyzer ?? new LightweightSqlAnalyzer();
        _buffer = buffer ?? new InMemoryTransactionsBuffer();
        _logger = logger;
        Metrics = metrics ?? new HihooiMetrics();
        Events = events ?? new HihooiEventStore();
        if (!string.IsNullOrWhiteSpace(options.RecoveryJournalDirectory))
        {
            _journal = journal ?? new FileRecoveryJournal(options.RecoveryJournalDirectory);
            _appliedStore = appliedStore ?? new FileAppliedTsIdStore(options.RecoveryJournalDirectory);
        }
        else
        {
            _journal = journal;
            _appliedStore = appliedStore;
        }
        Router = new QueryRouter(_indexes, _extensions, () => _tsIdAssigner.LatestAssigned, Metrics);
    }

    public HihooiOptions Options { get; }
    public IDatabaseProvider Provider => _provider;
    public ExtensionRegistry Extensions => _extensions;
    public QueryRouter Router { get; }
    public ReplicaIndexes Indexes => _indexes;
    public HihooiMetrics Metrics { get; }
    public HihooiEventStore Events { get; }
    public long LatestTsId => _tsIdAssigner.LatestAssigned;
    public long PendingPublishedEstimate => Math.Max(0, LatestTsId - _extensions.MinAppliedTsId());

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_started) return;

            if (Options.EnsureSnapshotIsolationOnStart)
            {
                await _provider.EnsureSnapshotIsolationAsync(Options.PrimaryConnectionString, cancellationToken).ConfigureAwait(false);
                foreach (var ext in Options.Extensions)
                    await _provider.EnsureSnapshotIsolationAsync(ext.ConnectionString, cancellationToken).ConfigureAwait(false);
            }

            foreach (var ext in Options.Extensions)
                StartExtractor(ext.Id, ext.ConnectionString);

            if (_journal is not null)
                await RecoverFromJournalAsync(cancellationToken).ConfigureAwait(false);

            _started = true;
        }
        finally
        {
            _startGate.Release();
        }
    }

    public HihooiSession OpenSession(ConsistencyLevel? consistency = null)
        => new(this, _analyzer, consistency ?? Options.DefaultConsistencyLevel);

    internal Task<IDatabaseSession> OpenPrimarySessionAsync(CancellationToken ct)
        => _provider.OpenSessionAsync(Options.PrimaryConnectionString, "primary", ct);

    internal async Task CompleteWriteTransactionAsync(
        TransactionState building,
        IDatabaseSession primarySession,
        List<SqlStatementInfo> writeStatements,
        ReadWriteSets runningSets,
        TimeSpan elapsed,
        CompletionOperation completion,
        bool isFailed,
        ConsistencyLevel consistency,
        CancellationToken cancellationToken)
    {
        _tsIdAssigner.BeginCommitAttempt();
        DateTime commitTs;
        try
        {
            commitTs = await primarySession.GetEngineUtcTimestampAsync(cancellationToken).ConfigureAwait(false);
            if (completion == CompletionOperation.Commit)
                await primarySession.CommitAsync(cancellationToken).ConfigureAwait(false);
            else
                await primarySession.RollbackAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Metrics.RecordError($"Primary completion failed: {ex.Message}");
            var failed = CloneState(building, writeStatements, runningSets, elapsed, CompletionOperation.Rollback, DateTime.UtcNow, isFailed: true);
            await _tsIdAssigner.CompleteAsync(failed).ConfigureAwait(false);
            throw;
        }

        var state = CloneState(building, writeStatements, runningSets, elapsed, completion, commitTs, isFailed);
        var tsId = await _tsIdAssigner.CompleteAsync(state).ConfigureAwait(false);
        state.TsId = tsId;
        Events.Emit(HihooiEventType.TsIdGenerated, "TsIdAssigner", $"Assigned TSID {tsId}",
            state.TransactionId, tsId, elapsed.TotalMilliseconds);

        if (completion == CompletionOperation.Commit)
        {
            _indexes.UpdateIndexes(state);
            Metrics.RecordCommit();
            Events.Emit(HihooiEventType.TransactionCommitted, "TransactionManager", "Commit",
                state.TransactionId, tsId, elapsed.TotalMilliseconds, "Completed");
        }
        else
        {
            Metrics.RecordRollback();
            Events.Emit(HihooiEventType.TransactionRolledBack, "TransactionManager", "Rollback",
                state.TransactionId, tsId, elapsed.TotalMilliseconds, "Completed");
        }

        if (_journal is not null)
        {
            await _journal.AppendAsync(new JournalEntry
            {
                TsId = tsId,
                TransactionId = state.TransactionId,
                Completion = completion,
                Status = JournalEntryStatus.PendingPublish,
                PayloadJson = FileRecoveryJournal.SerializeState(state)
            }, cancellationToken).ConfigureAwait(false);
        }

        await _buffer.PublishAsync(state, cancellationToken).ConfigureAwait(false);
        Events.Emit(HihooiEventType.BufferEnqueued, "Buffer", $"Published TSID {tsId}",
            state.TransactionId, tsId, status: "Completed");

        if (_journal is not null)
            await _journal.UpdateStatusAsync(tsId, JournalEntryStatus.Published, cancellationToken).ConfigureAwait(false);

        RefreshExtensionLag();

        if (consistency == ConsistencyLevel.OneCopySerializability)
            await WaitUntilAppliedOnAllAsync(tsId, cancellationToken).ConfigureAwait(false);

        var minApplied = _extensions.MinAppliedTsId();
        _indexes.PruneRowsAppliedEverywhere(minApplied);
        if (_journal is not null && minApplied >= tsId)
            await _journal.UpdateStatusAsync(tsId, JournalEntryStatus.AppliedEverywhere, cancellationToken).ConfigureAwait(false);
    }

    private async Task RecoverFromJournalAsync(CancellationToken ct)
    {
        Events.Emit(HihooiEventType.RecoveryStarted, "Recovery", "Journal replay starting", status: "Running");
        try
        {
            var pending = await _journal!.GetPendingAsync(ct).ConfigureAwait(false);
            // Republish only non-fully-applied entries. Extractors load durable watermarks to skip already-applied TSIDs.
            foreach (var entry in pending.Where(e =>
                         e.Status is JournalEntryStatus.PendingPublish or JournalEntryStatus.Published))
            {
                var state = FileRecoveryJournal.DeserializeState(entry.PayloadJson);
                if (state is null) continue;
                await _buffer.PublishAsync(state, ct).ConfigureAwait(false);
                await _journal.UpdateStatusAsync(entry.TsId, JournalEntryStatus.Published, ct).ConfigureAwait(false);
                Metrics.RecordError($"Recovered TSID {entry.TsId} from journal (republish)");
            }
            Events.Emit(HihooiEventType.RecoveryCompleted, "Recovery",
                $"Republished {pending.Count} journal entries", status: "Completed");
        }
        catch (Exception ex)
        {
            Events.Emit(HihooiEventType.RecoveryFailed, "Recovery", ex.Message, status: "Failed");
            throw;
        }
    }

    private void RefreshExtensionLag()
    {
        var latest = LatestTsId;
        foreach (var e in _extensions.Snapshot())
        {
            Metrics.UpsertExtension(e.Id, m =>
            {
                m.IsOnline = e.IsOnline;
                m.AppliedTsId = e.AppliedTsId;
                m.Lag = Math.Max(0, latest - e.AppliedTsId);
                m.InflightReads = e.InflightReads;
            });
        }
    }

    private async Task WaitUntilAppliedOnAllAsync(long tsId, CancellationToken ct)
    {
        var extensions = _extensions.Snapshot();
        if (extensions.Count == 0) return;

        while (!ct.IsCancellationRequested)
        {
            extensions = _extensions.Snapshot();
            if (extensions.Any(e => !e.IsOnline))
                throw new InvalidOperationException(
                    "One-copy serializability requires all Extensions to be online for synchronous apply.");

            if (extensions.All(e => e.AppliedTsId >= tsId))
                return;
            await Task.Delay(5, ct).ConfigureAwait(false);
        }
        ct.ThrowIfCancellationRequested();
    }

    private static TransactionState CloneState(
        TransactionState building,
        List<SqlStatementInfo> writes,
        ReadWriteSets sets,
        TimeSpan elapsed,
        CompletionOperation completion,
        DateTime commitTs,
        bool isFailed)
    {
        var state = new TransactionState
        {
            TransactionId = building.TransactionId,
            PrimaryExecutionTime = elapsed,
            Completion = completion,
            CommitTimestampUtc = commitTs,
            IsFailed = isFailed
        };
        state.Sets.Merge(sets);
        foreach (var w in writes) state.WriteStatements.Add(w);
        return state;
    }

    public void MarkExtensionOffline(string id)
    {
        _extensions.SetOnline(id, false);
        Metrics.UpsertExtension(id, m => m.IsOnline = false);
        Metrics.RecordError($"Extension {id} marked offline");
    }

    public void MarkExtensionOnline(string id)
    {
        _extensions.SetOnline(id, true);
        Metrics.UpsertExtension(id, m => m.IsOnline = true);
    }

    public void AddExtension(string id, string connectionString)
    {
        lock (_extractorGate)
            StartExtractor(id, connectionString);
    }

    private void StartExtractor(string id, string connectionString)
    {
        _extensions.Register(new ExtensionInfo { Id = id, ConnectionString = connectionString });
        Metrics.UpsertExtension(id, m => { m.IsOnline = true; m.AppliedTsId = 0; });
        var extractor = new ExtractorService(
            id, connectionString, _provider, _buffer, _extensions, _logger, Metrics, _appliedStore);
        extractor.Start();
        lock (_extractorGate) _extractors.Add(extractor);
    }

    public async ValueTask DisposeAsync()
    {
        List<ExtractorService> copy;
        lock (_extractorGate)
        {
            copy = _extractors.ToList();
            _extractors.Clear();
        }
        foreach (var e in copy)
            await e.DisposeAsync().ConfigureAwait(false);
        _startGate.Dispose();
    }
}
