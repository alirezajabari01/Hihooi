namespace Hihooi.Core.Diagnostics.Health;

/// <summary>Used in unit tests / when a real probe is not registered.</summary>
public sealed class AlwaysHealthyProbe : INodeHealthProbe
{
    public Task<(bool Ok, double LatencyMs, string? Error)> ProbeAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
        => Task.FromResult<(bool, double, string?)>((true, 0.1, null));
}
