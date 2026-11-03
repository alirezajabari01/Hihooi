using Hihooi.Core.Buffer;
using Hihooi.Core.Model;
using Hihooi.Core.Routing;

namespace Hihooi.Core.Archiver;

/// <summary>
/// Moves TStates applied on all Extensions from the in-memory buffer log into a persistent archive list (paper §3, §6.1).
/// Seed DB creation/restore remains engine-specific (see SQL Server docs).
/// </summary>
public sealed class ArchiverService
{
    private readonly InMemoryTransactionsBuffer _buffer;
    private readonly ExtensionRegistry _extensions;
    private readonly List<TransactionState> _archiverBuffer = new();
    private readonly object _gate = new();
    private long _lastArchivedTsId;
    public long SeedAssociatedTsId { get; private set; }

    public ArchiverService(InMemoryTransactionsBuffer buffer, ExtensionRegistry extensions)
    {
        _buffer = buffer;
        _extensions = extensions;
    }

    public IReadOnlyList<TransactionState> ArchiverBuffer
    {
        get { lock (_gate) return _archiverBuffer.ToList(); }
    }

    public void SpillAppliedEverywhere()
    {
        var minApplied = _extensions.MinAppliedTsId();
        if (minApplied <= _lastArchivedTsId) return;

        var log = _buffer.SnapshotLog();
        lock (_gate)
        {
            foreach (var s in log.Where(s => s.TsId > _lastArchivedTsId && s.TsId <= minApplied))
                _archiverBuffer.Add(s);
            _lastArchivedTsId = minApplied;
        }
    }

    public void OnSeedCreated(long associatedTsId)
    {
        SeedAssociatedTsId = associatedTsId;
        lock (_gate)
        {
            _archiverBuffer.RemoveAll(s => s.TsId <= associatedTsId);
        }
    }
}
