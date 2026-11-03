using Microsoft.Extensions.Hosting;

namespace Hihooi;

/// <summary>Starts the Hihooi engine when the host starts (safe no-op if already started).</summary>
internal sealed class HihooiRuntimeHostedService : IHostedService
{
    private readonly HihooiDatabase _database;

    public HihooiRuntimeHostedService(HihooiDatabase database)
    {
        _database = database;
    }

    public Task StartAsync(CancellationToken cancellationToken)
        => _database.EnsureStartedAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;
}
