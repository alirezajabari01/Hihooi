using System.Diagnostics;
using System.Threading.Channels;
using Hihooi.Core.Configuration;
using Hihooi.Core.Parsing;

namespace Hihooi.Core.Diagnostics.Tracing;

public interface IHihooiTracer
{
    bool IsEnabled { get; }
    TraceScope? StartRequest(string operation, RequestKind kind, string? sql = null, object? parameters = null);
    void RecordInFlight(HihooiTrace trace);
    void Complete(HihooiTrace trace);
    IHihooiTraceStore Store { get; }
}

public sealed class TraceScope : IDisposable
{
    private readonly HihooiTracer _tracer;
    private readonly Activity? _activity;
    private int _disposed;

    internal TraceScope(HihooiTracer tracer, HihooiTrace trace, Activity? activity)
    {
        _tracer = tracer;
        Trace = trace;
        _activity = activity;
        RootSpan = new HihooiSpan
        {
            SpanId = NewId(),
            ParentSpanId = null,
            Name = "api.entry",
            Component = "HihooiDatabase",
            StartUtc = DateTime.UtcNow,
            Status = SpanStatus.Unset
        };
        Trace.Spans.Add(RootSpan);
    }

    public HihooiTrace Trace { get; }
    public HihooiSpan RootSpan { get; }

    public HihooiSpan StartSpan(string name, string component, string? parentSpanId = null, string? nodeId = null)
    {
        var span = new HihooiSpan
        {
            SpanId = NewId(),
            ParentSpanId = parentSpanId ?? RootSpan.SpanId,
            Name = name,
            Component = component,
            StartUtc = DateTime.UtcNow,
            NodeId = nodeId,
            Status = SpanStatus.Unset
        };
        Trace.Spans.Add(span);
        return span;
    }

    public void EndSpan(HihooiSpan span, SpanStatus status = SpanStatus.Ok, string? error = null, Action<HihooiSpan>? enrich = null)
    {
        span.EndUtc = DateTime.UtcNow;
        span.DurationMs = (span.EndUtc.Value - span.StartUtc).TotalMilliseconds;
        span.Status = status;
        if (error is not null) span.ErrorMessage = error;
        enrich?.Invoke(span);
    }

    public void SetAttr(HihooiSpan span, string key, string? value)
    {
        if (string.IsNullOrEmpty(key)) return;
        span.Attributes[key] = TraceMasking.Truncate(value, Math.Max(64, _tracer.Options.MaxAttributeBytes / 8));
    }

    public void Fail(string message)
    {
        Trace.Status = SpanStatus.Error;
        Trace.ErrorMessage = message;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            if (RootSpan.EndUtc is null)
                EndSpan(RootSpan, Trace.Status == SpanStatus.Error ? SpanStatus.Error : SpanStatus.Ok, Trace.ErrorMessage);
            Trace.EndUtc ??= DateTime.UtcNow;
            Trace.DurationMs = (Trace.EndUtc.Value - Trace.StartUtc).TotalMilliseconds;
            if (Trace.Status == SpanStatus.Unset)
                Trace.Status = SpanStatus.Ok;
            _tracer.Complete(Trace);
        }
        catch
        {
            // never throw from dispose path
        }
        finally
        {
            _activity?.Dispose();
        }
    }

    internal static string NewId() => Guid.NewGuid().ToString("N")[..16];
}

public sealed class HihooiTracer : IHihooiTracer, IAsyncDisposable
{
    private static readonly ActivitySource Source = new("Hihooi");
    private readonly IHihooiTraceStore _store;
    private readonly Channel<HihooiTrace> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;
    private long _dropped;

    public HihooiTracer(ObservabilityOptions options, IHihooiTraceStore store)
    {
        Options = options;
        _store = store;
        var capacity = Math.Max(64, options.ChannelCapacity);
        _channel = Channel.CreateBounded<HihooiTrace>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        _worker = Task.Run(ProcessAsync);
    }

    public ObservabilityOptions Options { get; }
    public IHihooiTraceStore Store => _store;
    public bool IsEnabled => Options.Enabled;
    public long DroppedCount => Interlocked.Read(ref _dropped);

    public TraceScope? StartRequest(string operation, RequestKind kind, string? sql = null, object? parameters = null)
    {
        if (!Options.Enabled) return null;
        if (Options.SamplingRate < 1.0 && Random.Shared.NextDouble() > Options.SamplingRate)
            return null;

        try
        {
            var parent = Activity.Current;
            var activity = Source.StartActivity(operation, ActivityKind.Internal, parent?.Context ?? default);
            var traceId = activity?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");

            var bound = SqlParameterBinder.Bind(parameters);
            var masked = Options.CaptureParameters
                ? TraceMasking.MaskParameters(bound, Options.MaskSensitiveParameters, Options.MaxAttributeBytes)
                : null;

            var trace = new HihooiTrace
            {
                TraceId = traceId,
                RequestId = Guid.NewGuid().ToString("N"),
                ParentTraceId = parent is null ? null : parent.TraceId.ToString(),
                Operation = operation,
                Kind = kind,
                StartUtc = DateTime.UtcNow,
                Status = SpanStatus.Unset,
                Sql = Options.CaptureSql ? TraceMasking.Truncate(sql, Options.MaxAttributeBytes) : null,
                SqlShape = SqlShapeNormalizer.Normalize(sql),
                Parameters = masked,
                ApplicationName = Options.ApplicationName,
                ApplicationInstance = Options.ApplicationInstance ?? Environment.MachineName,
                Incomplete = true
            };

            var scope = new TraceScope(this, trace, activity);
            RecordInFlight(trace);
            return scope;
        }
        catch
        {
            return null;
        }
    }

    public void RecordInFlight(HihooiTrace trace)
    {
        try { _store.UpsertInFlight(trace); }
        catch { /* never break callers */ }
    }

    public void Complete(HihooiTrace trace)
    {
        try
        {
            trace.Incomplete = false;
            if (!_channel.Writer.TryWrite(trace))
                Interlocked.Increment(ref _dropped);
        }
        catch
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    private async Task ProcessAsync()
    {
        try
        {
            await foreach (var trace in _channel.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                try { _store.Complete(trace); }
                catch { Interlocked.Increment(ref _dropped); }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        try { await _worker.ConfigureAwait(false); } catch { /* ignore */ }
        _cts.Dispose();
        if (_store is IAsyncDisposable ad)
            await ad.DisposeAsync().ConfigureAwait(false);
    }
}

public sealed class NullHihooiTracer : IHihooiTracer
{
    public static readonly NullHihooiTracer Instance = new();
    private readonly InMemoryTraceStore _store = new(capacity: 16);
    public bool IsEnabled => false;
    public IHihooiTraceStore Store => _store;
    public TraceScope? StartRequest(string operation, RequestKind kind, string? sql = null, object? parameters = null) => null;
    public void RecordInFlight(HihooiTrace trace) { }
    public void Complete(HihooiTrace trace) { }
}
