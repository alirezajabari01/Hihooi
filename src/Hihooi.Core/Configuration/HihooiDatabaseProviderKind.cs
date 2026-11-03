namespace Hihooi.Core.Configuration;

/// <summary>
/// Selected database provider for the Hihooi middleware.
/// Only <see cref="SqlServer"/> is implemented in this package; other values are reserved for future providers.
/// </summary>
public enum HihooiDatabaseProviderKind
{
    Unspecified = 0,
    SqlServer = 1,
    /// <summary>Not implemented. Calling UsePostgreSQL throws.</summary>
    PostgreSql = 2,
    /// <summary>Not implemented. Calling UseMySql throws.</summary>
    MySql = 3
}
