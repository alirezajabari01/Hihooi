namespace Hihooi.Core.Diagnostics.Tracing;

/// <summary>Ambient trace scope for the current async flow (set by IHihooiDatabase).</summary>
public static class HihooiTraceContext
{
    private static readonly AsyncLocal<TraceScope?> Current = new();

    public static TraceScope? Scope
    {
        get => Current.Value;
        set => Current.Value = value;
    }
}
