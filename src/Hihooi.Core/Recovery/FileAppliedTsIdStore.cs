namespace Hihooi.Core.Recovery;

/// <summary>Persists per-Extension contiguous applied TSID to avoid re-apply after restart.</summary>
public interface IAppliedTsIdStore
{
    Task<long> LoadAsync(string extensionId, CancellationToken cancellationToken = default);
    Task SaveAsync(string extensionId, long contiguousTsId, CancellationToken cancellationToken = default);
}

public sealed class FileAppliedTsIdStore : IAppliedTsIdStore
{
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileAppliedTsIdStore(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    private string PathFor(string extensionId)
    {
        var safe = string.Join("_", extensionId.Split(System.IO.Path.GetInvalidFileNameChars()));
        return System.IO.Path.Combine(_directory, $"applied-{safe}.tsid");
    }

    public async Task<long> LoadAsync(string extensionId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = PathFor(extensionId);
            if (!File.Exists(path)) return 0;
            var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return long.TryParse(text.Trim(), out var v) && v > 0 ? v : 0;
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(string extensionId, long contiguousTsId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await File.WriteAllTextAsync(PathFor(extensionId), contiguousTsId.ToString(), cancellationToken)
                .ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
}
