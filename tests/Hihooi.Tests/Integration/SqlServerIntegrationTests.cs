using Hihooi.Core.Configuration;
using Hihooi.Core.Consistency;
using Hihooi.Core.TransactionManager;
using Hihooi.SqlServer;

namespace Hihooi.Tests.Integration;

/// <summary>
/// Real SQL Server tests. Set HIHOOI_SQLSERVER_PRIMARY and HIHOOI_SQLSERVER_EXT1 connection strings to run.
/// </summary>
public class SqlServerIntegrationTests
{
    private static bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HIHOOI_SQLSERVER_PRIMARY"))
        && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HIHOOI_SQLSERVER_EXT1"));

    [Fact]
    public async Task Primary_write_replicates_when_sqlserver_available()
    {
        if (!IsConfigured)
        {
            // Documented skip: environment has no SQL Server endpoints configured.
            return;
        }

        var primary = Environment.GetEnvironmentVariable("HIHOOI_SQLSERVER_PRIMARY")!;
        var ext1 = Environment.GetEnvironmentVariable("HIHOOI_SQLSERVER_EXT1")!;
        var provider = new SqlServerDatabaseProvider();
        var options = new HihooiOptions
        {
            PrimaryConnectionString = primary,
            Extensions = { new ExtensionEndpoint { Id = "ext1", ConnectionString = ext1 } },
            EnsureSnapshotIsolationOnStart = true,
            DefaultConsistencyLevel = ConsistencyLevel.Gssi
        };

        await using var tm = new HihooiTransactionManager(options, provider);
        await tm.StartAsync();

        var table = "HihooiTest_" + Guid.NewGuid().ToString("N")[..8];
        await using (var setup = await provider.OpenSessionAsync(primary, "primary"))
        {
            await setup.ExecuteNonQueryAsync($"CREATE TABLE {table} (Id INT PRIMARY KEY, Name NVARCHAR(50))");
        }
        await using (var setupExt = await provider.OpenSessionAsync(ext1, "ext1"))
        {
            await setupExt.ExecuteNonQueryAsync($"CREATE TABLE {table} (Id INT PRIMARY KEY, Name NVARCHAR(50))");
        }

        try
        {
            await using var session = tm.OpenSession();
            await session.ExecuteNonQueryAsync($"INSERT INTO {table} (Id, Name) VALUES (1, 'ok')");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(10) && tm.Extensions.Snapshot().First().AppliedTsId < 1)
                await Task.Delay(50);

            Assert.True(tm.Extensions.Snapshot().First().AppliedTsId >= 1);
            await using var read = await provider.OpenSessionAsync(ext1, "ext1");
            var rows = await read.ExecuteReaderAsync($"SELECT Name FROM {table} WHERE Id = 1");
            Assert.Single(rows);
        }
        finally
        {
            await using var dropP = await provider.OpenSessionAsync(primary, "primary");
            await dropP.ExecuteNonQueryAsync($"DROP TABLE IF EXISTS {table}");
            await using var dropE = await provider.OpenSessionAsync(ext1, "ext1");
            await dropE.ExecuteNonQueryAsync($"DROP TABLE IF EXISTS {table}");
        }
    }
}
