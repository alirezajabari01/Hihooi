using Hihooi.Core.Abstractions;
using Microsoft.Data.SqlClient;

namespace Hihooi.SqlServer;

public sealed class SqlServerDatabaseProvider : IDatabaseProvider
{
    private readonly int _commandTimeoutSeconds;

    public SqlServerDatabaseProvider(int commandTimeoutSeconds = 30)
    {
        _commandTimeoutSeconds = commandTimeoutSeconds;
    }

    public string ProviderName => "SqlServer";

    public async Task<IDatabaseSession> OpenSessionAsync(string connectionString, string replicaId, CancellationToken cancellationToken = default)
    {
        try
        {
            var conn = new SqlConnection(connectionString);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            return new SqlServerDatabaseSession(conn, replicaId, _commandTimeoutSeconds);
        }
        catch (SqlException ex)
        {
            throw new InvalidOperationException($"Failed to open SQL Server connection for replica '{replicaId}': {ex.Message}", ex);
        }
    }

    public async Task EnsureSnapshotIsolationAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        var db = conn.Database;
        if (string.IsNullOrWhiteSpace(db))
            throw new InvalidOperationException("Connection string must specify an Initial Catalog/Database for Snapshot Isolation setup.");

        await using (var alter = conn.CreateCommand())
        {
            alter.CommandText = @"
IF EXISTS (SELECT 1 FROM sys.databases WHERE name = @db AND snapshot_isolation_state_desc <> 'ON')
BEGIN
    DECLARE @sql nvarchar(max) = N'ALTER DATABASE ' + QUOTENAME(@db) + N' SET ALLOW_SNAPSHOT_ISOLATION ON';
    EXEC(@sql);
END
IF EXISTS (SELECT 1 FROM sys.databases WHERE name = @db AND is_read_committed_snapshot_on = 0)
BEGIN
    DECLARE @sql2 nvarchar(max) = N'ALTER DATABASE ' + QUOTENAME(@db) + N' SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE';
    EXEC(@sql2);
END";
            alter.Parameters.AddWithValue("@db", db);
            try
            {
                await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SqlException ex)
            {
                throw new InvalidOperationException(
                    $"Failed to enable Snapshot Isolation on database '{db}'. Preconfigure ALLOW_SNAPSHOT_ISOLATION ON, or set EnsureSnapshotIsolationOnStart=false only if SI is already enabled.",
                    ex);
            }
        }

        await using var check = conn.CreateCommand();
        check.CommandText = "SELECT snapshot_isolation_state_desc FROM sys.databases WHERE name = @db";
        check.Parameters.AddWithValue("@db", db);
        var state = Convert.ToString(await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        if (!string.Equals(state, "ON", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Snapshot Isolation is not ON for database '{db}' (state='{state}'). GSSI guarantees require Strong SI on each replica.");
        }
    }
}
