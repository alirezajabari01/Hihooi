using Hihooi.Core.Abstractions;
using Hihooi.Core.Algorithms;
using Hihooi.Core.Diagnostics;
using Hihooi.Core.Model;
using Hihooi.Core.Recovery;
using Hihooi.Core.Routing;
using Hihooi.Core.Tsid;
using Microsoft.Extensions.Logging;

namespace Hihooi.Core.Replication;

public sealed class ExtractorService : IAsyncDisposable
{
    private readonly string _extensionId;
    private readonly string _connectionString;
    private readonly IDatabaseProvider _provider;
    private readonly ITransactionsBuffer _buffer;
    private readonly ExtensionRegistry _registry;
    private readonly ContiguousTsIdTracker _tracker = new();
    private readonly ParallelApplyScheduler _scheduler = new();
    private readonly ILogger? _logger;
    private readonly HihooiMetrics? _metrics;
    private readonly IAppliedTsIdStore? _appliedStore;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private readonly SemaphoreSlim _applyGate = new(Environment.ProcessorCount);
    private readonly HashSet<long> _appliedExact = new();
    private readonly object _dedupeGate = new();

    public ExtractorService(
        string extensionId,
        string connectionString,
        IDatabaseProvider provider,
        ITransactionsBuffer buffer,
        ExtensionRegistry registry,
        ILogger? logger = null,
        HihooiMetrics? metrics = null,
        IAppliedTsIdStore? appliedStore = null)
    {
        _extensionId = extensionId;
        _connectionString = connectionString;
        _provider = provider;
        _buffer = buffer;
        _registry = registry;
        _logger = logger;
        _metrics = metrics;
        _appliedStore = appliedStore;
    }

    public long ContiguousApplied => _tracker.ContiguousApplied;

    public void Start()
    {
        if (_loop is not null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            if (_appliedStore is not null)
            {
                var watermark = await _appliedStore.LoadAsync(_extensionId, ct).ConfigureAwait(false);
                if (watermark > 0)
                {
                    _tracker.Reset(watermark);
                    _registry.SetAppliedTsId(_extensionId, watermark);
                    await _buffer.AcknowledgeAppliedAsync(_extensionId, watermark, ct).ConfigureAwait(false);
                }
            }

            await foreach (var state in _buffer.SubscribeAsync(_extensionId, ct).ConfigureAwait(false))
            {
                lock (_dedupeGate)
                {
                    if (_appliedExact.Contains(state.TsId) || state.TsId <= _tracker.ContiguousApplied)
                        continue;
                }

                var ready = _scheduler.OnNewTransaction(state);
                if (ready.Count == 0)
                    _metrics?.RecordConflict();
                foreach (var ts in ready)
                {
                    _metrics?.RecordParallelApply();
                    _ = ApplyAndContinueAsync(ts, ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Extractor {Id} failed", _extensionId);
            _registry.SetOnline(_extensionId, false);
            _metrics?.UpsertExtension(_extensionId, m => m.IsOnline = false);
            _metrics?.RecordError($"Extractor {_extensionId}: {ex.Message}");
        }
    }

    private async Task ApplyAndContinueAsync(TransactionState ts, CancellationToken ct)
    {
        await _applyGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var applied = false;
            try
            {
                await ApplyOneAsync(ts, ct).ConfigureAwait(false);
                applied = true;
                lock (_dedupeGate) _appliedExact.Add(ts.TsId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogError(ex, "Apply failed for TSID {TsId} on {ExtensionId}", ts.TsId, _extensionId);
                _registry.SetOnline(_extensionId, false);
                _metrics?.RecordFailedApply();
                _metrics?.UpsertExtension(_extensionId, m =>
                {
                    m.IsOnline = false;
                    m.ApplyFailures++;
                });
                _metrics?.RecordError($"Apply TSID {ts.TsId} on {_extensionId}: {ex.Message}");
            }

            if (applied)
            {
                var contiguous = _tracker.ReportCompleted(ts.TsId);
                _registry.SetAppliedTsId(_extensionId, contiguous);
                await _buffer.AcknowledgeAppliedAsync(_extensionId, contiguous, ct).ConfigureAwait(false);
                if (_appliedStore is not null)
                    await _appliedStore.SaveAsync(_extensionId, contiguous, ct).ConfigureAwait(false);
                _metrics?.UpsertExtension(_extensionId, m =>
                {
                    m.AppliedTsId = contiguous;
                    m.IsOnline = true;
                });
            }

            var next = _scheduler.OnTransactionComplete(ts);
            foreach (var n in next)
            {
                _metrics?.RecordParallelApply();
                _ = ApplyAndContinueAsync(n, ct);
            }
        }
        finally
        {
            _applyGate.Release();
        }
    }

    private async Task ApplyOneAsync(TransactionState ts, CancellationToken ct)
    {
        await using var session = await _provider.OpenSessionAsync(_connectionString, _extensionId, ct).ConfigureAwait(false);
        await session.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            if (ts.Completion == CompletionOperation.Rollback || ts.IsFailed)
            {
                foreach (var stmt in ts.WriteStatements)
                {
                    try
                    {
                        await session.ExecuteNonQueryAsync(stmt.RewrittenSql ?? stmt.Sql, stmt.Parameters, ct).ConfigureAwait(false);
                    }
                    catch { /* Table IV: best-effort sequence advance */ }
                }
                await session.RollbackAsync(ct).ConfigureAwait(false);
                return;
            }

            foreach (var stmt in ts.WriteStatements)
                await session.ExecuteNonQueryAsync(stmt.RewrittenSql ?? stmt.Sql, stmt.Parameters, ct).ConfigureAwait(false);
            await session.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            try { await session.RollbackAsync(ct).ConfigureAwait(false); } catch { /* ignore */ }
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is null) return;
        _cts.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch { /* ignore */ }
        }
        _cts.Dispose();
        _applyGate.Dispose();
    }
}
