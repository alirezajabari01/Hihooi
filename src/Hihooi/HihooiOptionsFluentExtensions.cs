using Hihooi.Core.Configuration;
using Hihooi.Core.Consistency;

namespace Hihooi;

/// <summary>Fluent configuration helpers for <see cref="HihooiOptions"/>.</summary>
public static class HihooiOptionsFluentExtensions
{
    /// <summary>Select the SQL Server provider (currently the only implemented provider).</summary>
    public static HihooiOptions UseSqlServer(this HihooiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.ProviderKind = HihooiDatabaseProviderKind.SqlServer;
        return options;
    }

    /// <summary>Reserved. PostgreSQL is not implemented in this package.</summary>
    public static HihooiOptions UsePostgreSQL(this HihooiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        throw new NotSupportedException(
            "PostgreSQL provider is not implemented. Use UseSqlServer() or wait for a future Hihooi release.");
    }

    /// <summary>Reserved. MySQL is not implemented in this package.</summary>
    public static HihooiOptions UseMySQL(this HihooiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        throw new NotSupportedException(
            "MySQL provider is not implemented. Use UseSqlServer() or wait for a future Hihooi release.");
    }

    public static HihooiOptions SetPrimary(this HihooiOptions options, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        options.PrimaryConnectionString = connectionString;
        return options;
    }

    /// <summary>Add a replica (extension) endpoint. Id defaults to ext1, ext2, …</summary>
    public static HihooiOptions AddReplica(this HihooiOptions options, string connectionString, string? id = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var replicaId = string.IsNullOrWhiteSpace(id)
            ? "ext" + (options.Extensions.Count + 1)
            : id.Trim();
        options.Extensions.Add(new ExtensionEndpoint { Id = replicaId, ConnectionString = connectionString });
        return options;
    }

    public static HihooiOptions UseConsistency(this HihooiOptions options, ConsistencyLevel level)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.ConsistencyMode = level;
        return options;
    }

    public static HihooiOptions UseRedisBuffer(this HihooiOptions options, string redisConnectionString, string? keyPrefix = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(redisConnectionString);
        options.RedisConnectionString = redisConnectionString;
        if (!string.IsNullOrWhiteSpace(keyPrefix))
            options.RedisKeyPrefix = keyPrefix;
        return options;
    }
}
