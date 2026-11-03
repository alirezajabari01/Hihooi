using System.Collections.Concurrent;
using System.Text.Json;

namespace Hihooi.Core.Diagnostics.Tracing;

/// <summary>Append-only JSONL persistence layered over an in-memory index.</summary>
public sealed class FileTraceStore : IHihooiTraceStore, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };
    private readonly InMemoryTraceStore _memory;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentQueue<HihooiTrace> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _flushLoop;
    private readonly TimeSpan _retention;

    public FileTraceStore(string directory, int capacity = 2000, TimeSpan? retention = null)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "hihooi-traces.jsonl");
        _retention = retention ?? TimeSpan.FromHours(24);
        _memory = new InMemoryTraceStore(capacity, _retention);
        LoadExisting();
        _flushLoop = Task.Run(FlushLoopAsync);
    }

    public void UpsertInFlight(HihooiTrace trace) => _memory.UpsertInFlight(trace);

    public void Complete(HihooiTrace trace)
    {
        _memory.Complete(trace);
        _pending.Enqueue(trace);
    }

    public HihooiTrace? Get(string traceId) => _memory.Get(traceId);

    public IReadOnlyList<HihooiTrace> Query(
        int take = 100,
        RequestKind? kind = null,
        SpanStatus? status = null,
        string? nodeId = null,
        string? consistencyMode = null,
        string? traceIdContains = null,
        bool? inFlight = null,
        double? minDurationMs = null)
        => _memory.Query(take, kind, status, nodeId, consistencyMode, traceIdContains, inFlight, minDurationMs);

    public IReadOnlyList<TraceTrafficItem> RecentTraffic(int take = 100) => _memory.RecentTraffic(take);
    public TraceAnalyticsSnapshot Analytics() => _memory.Analytics();

    private void LoadExisting()
    {
        if (!File.Exists(_path)) return;
        var cutoff = DateTime.UtcNow - _retention;
        try
        {
            foreach (var line in File.ReadLines(_path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                HihooiTrace? t;
                try { t = JsonSerializer.Deserialize<HihooiTrace>(line, JsonOpts); }
                catch (JsonException) { continue; }
                if (t is null || t.StartUtc < cutoff) continue;
                _memory.Complete(t);
            }
        }
        catch
        {
            // Corrupt file must not break startup.
        }
    }

    private async Task FlushLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                while (_pending.TryDequeue(out var t))
                {
                    await _gate.WaitAsync(_cts.Token).ConfigureAwait(false);
                    try
                    {
                        var line = JsonSerializer.Serialize(t, JsonOpts);
                        await File.AppendAllTextAsync(_path, line + Environment.NewLine, _cts.Token).ConfigureAwait(false);
                    }
                    finally { _gate.Release(); }
                }
                await Task.Delay(200, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch
            {
                try { await Task.Delay(500, _cts.Token).ConfigureAwait(false); } catch { break; }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        try { await _flushLoop.ConfigureAwait(false); } catch { /* ignore */ }
        // Drain remaining
        while (_pending.TryDequeue(out var t))
        {
            try
            {
                var line = JsonSerializer.Serialize(t, JsonOpts);
                await File.AppendAllTextAsync(_path, line + Environment.NewLine).ConfigureAwait(false);
            }
            catch { /* ignore */ }
        }
        _cts.Dispose();
        _gate.Dispose();
    }
}
