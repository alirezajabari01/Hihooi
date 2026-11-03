using System.Diagnostics;
using Hihooi.Core.Diagnostics.Health;
using Microsoft.Data.SqlClient;

namespace Hihooi.SqlServer;

public sealed class SqlServerHealthProbe : INodeHealthProbe
{
    public async Task<(bool Ok, double LatencyMs, string? Error)> ProbeAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1";
            cmd.CommandTimeout = 2;
            _ = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return (true, sw.Elapsed.TotalMilliseconds, null);
        }
        catch (Exception ex)
        {
            return (false, sw.Elapsed.TotalMilliseconds, ex.Message);
        }
    }
}
