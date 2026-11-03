using System.Collections.Concurrent;

namespace Hihooi.Core.Diagnostics;

public enum HihooiEventType
{
    TransactionStarted,
    TransactionCommitted,
    TransactionRolledBack,
    TsIdGenerated,
    BufferEnqueued,
    ApplyStarted,
    ApplyCompleted,
    ApplyFailed,
    ExtensionConnected,
    ExtensionDisconnected,
    RecoveryStarted,
    RecoveryCompleted,
    RecoveryFailed,
    Error
}

public sealed class HihooiEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
    public string Component { get; init; } = "";
    public Guid? TransactionId { get; init; }
    public long? TsId { get; init; }
    public HihooiEventType EventType { get; init; }
    public double? DurationMs { get; init; }
    public string Status { get; init; } = "Info";
    public string Message { get; init; } = "";
}

/// <summary>Thread-safe ring buffer of recent diagnostic events for the dashboard.</summary>
public sealed class HihooiEventStore
{
    private readonly ConcurrentQueue<HihooiEvent> _events = new();
    private readonly int _capacity;
    private long _count;

    public HihooiEventStore(int capacity = 2000) => _capacity = Math.Max(100, capacity);

    public void Emit(HihooiEvent evt)
    {
        _events.Enqueue(evt);
        if (Interlocked.Increment(ref _count) > _capacity)
        {
            if (_events.TryDequeue(out _))
                Interlocked.Decrement(ref _count);
        }
    }

    public void Emit(
        HihooiEventType type,
        string component,
        string message,
        Guid? transactionId = null,
        long? tsId = null,
        double? durationMs = null,
        string status = "Info")
    {
        Emit(new HihooiEvent
        {
            EventType = type,
            Component = component,
            Message = message,
            TransactionId = transactionId,
            TsId = tsId,
            DurationMs = durationMs,
            Status = status
        });
    }

    public IReadOnlyList<HihooiEvent> Snapshot(int take = 200, string? typeFilter = null)
    {
        take = Math.Clamp(take, 1, _capacity);
        IEnumerable<HihooiEvent> q = _events.Reverse();
        if (!string.IsNullOrWhiteSpace(typeFilter)
            && Enum.TryParse<HihooiEventType>(typeFilter, true, out var parsed))
            q = q.Where(e => e.EventType == parsed);
        return q.Take(take).ToList();
    }
}
