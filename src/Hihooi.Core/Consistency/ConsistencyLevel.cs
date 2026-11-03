namespace Hihooi.Core.Consistency;

/// <summary>
/// Session-granularity consistency levels from Hihooi §5.3.
/// Default is <see cref="Gssi"/> (Global Strong Snapshot Isolation).
/// </summary>
public enum ConsistencyLevel
{
    /// <summary>Default Hihooi: async replication + fine-grained routing (Alg. 4).</summary>
    Gssi = 0,

    /// <summary>Async writes; reads may go to any Extension regardless of lag.</summary>
    WeakSi = 1,

    /// <summary>Async writes; reads only on fully caught-up Extensions (wait if none).</summary>
    RsiPc = 2,

    /// <summary>Synchronous apply to all Extensions before commit ack; reads on any Extension.</summary>
    OneCopySerializability = 3
}
