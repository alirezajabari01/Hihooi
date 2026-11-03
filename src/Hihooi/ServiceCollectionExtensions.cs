using Hihooi.Core.Abstractions;
using Hihooi.Core.Buffer;
using Hihooi.Core.Configuration;
using Hihooi.Core.Diagnostics;
using Hihooi.Core.Diagnostics.Health;
using Hihooi.Core.Diagnostics.Tracing;
using Hihooi.Core.Parsing;
using Hihooi.Core.Recovery;
using Hihooi.Core.TransactionManager;
using Hihooi.Redis;
using Hihooi.SqlServer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;

namespace Hihooi;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers Hihooi middleware and <see cref="IHihooiDatabase"/>.
    /// Prefer fluent options: <c>options.UseSqlServer().SetPrimary(...).AddReplica(...)</c>.
    /// Dashboard is separate: call <c>AddHihooiDashboard</c> / <c>MapHihooiDashboard</c> on ASP.NET hosts.
    /// </summary>
    public static IServiceCollection AddHihooi(this IServiceCollection services, Action<HihooiOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new HihooiOptions();
        configure(options);

        if (options.ProviderKind == HihooiDatabaseProviderKind.Unspecified)
            options.ProviderKind = HihooiDatabaseProviderKind.SqlServer;

        if (options.ProviderKind != HihooiDatabaseProviderKind.SqlServer)
        {
            throw new NotSupportedException(
                $"Provider '{options.ProviderKind}' is not implemented. Call options.UseSqlServer().");
        }

        if (string.IsNullOrWhiteSpace(options.PrimaryConnectionString))
            throw new InvalidOperationException("Primary connection string is required. Call options.SetPrimary(...).");

        services.AddSingleton(options);
        services.AddSingleton<HihooiMetrics>();
        services.AddSingleton<HihooiEventStore>();
        services.AddSingleton<ISqlAnalyzer, LightweightSqlAnalyzer>();

        services.AddSingleton<IHihooiTraceStore>(sp =>
        {
            var o = sp.GetRequiredService<HihooiOptions>().Observability;
            if (!string.IsNullOrWhiteSpace(o.StorageDirectory))
                return new FileTraceStore(o.StorageDirectory!, o.InMemoryCapacity, o.Retention);
            return new InMemoryTraceStore(o.InMemoryCapacity, o.Retention);
        });
        services.AddSingleton<IHihooiTracer>(sp =>
        {
            var o = sp.GetRequiredService<HihooiOptions>().Observability;
            if (!o.Enabled) return NullHihooiTracer.Instance;
            return new HihooiTracer(o, sp.GetRequiredService<IHihooiTraceStore>());
        });

        services.AddSingleton<INodeHealthProbe, SqlServerHealthProbe>();
        services.AddSingleton<INodeHealthMonitor>(sp =>
        {
            var opt = sp.GetRequiredService<HihooiOptions>();
            return new NodeHealthMonitor(
                sp.GetRequiredService<INodeHealthProbe>(),
                opt.Observability,
                () =>
                {
                    var list = new List<(string Id, string Role, string ConnectionString, string? Provider)>
                    {
                        ("primary", "Primary", opt.PrimaryConnectionString, "SqlServer")
                    };
                    foreach (var e in opt.Extensions)
                        list.Add((e.Id, "Extension", e.ConnectionString, "SqlServer"));
                    return list;
                });
        });

        if (!services.Any(d => d.ServiceType == typeof(ITransactionsBuffer)))
        {
            if (!string.IsNullOrWhiteSpace(options.RedisConnectionString))
            {
                services.AddSingleton<IConnectionMultiplexer>(_ =>
                    ConnectionMultiplexer.Connect(options.RedisConnectionString!));
                services.AddSingleton<ITransactionsBuffer>(sp =>
                    new RedisTransactionsBuffer(
                        sp.GetRequiredService<IConnectionMultiplexer>(),
                        options.RedisKeyPrefix));
            }
            else
            {
                services.AddSingleton<ITransactionsBuffer, InMemoryTransactionsBuffer>();
            }
        }

        services.AddSingleton<IDatabaseProvider>(sp =>
            new SqlServerDatabaseProvider(sp.GetRequiredService<HihooiOptions>().CommandTimeoutSeconds));

        if (!string.IsNullOrWhiteSpace(options.RecoveryJournalDirectory))
        {
            if (!services.Any(d => d.ServiceType == typeof(IRecoveryJournal)))
                services.AddSingleton<IRecoveryJournal>(_ => new FileRecoveryJournal(options.RecoveryJournalDirectory!));
            if (!services.Any(d => d.ServiceType == typeof(IAppliedTsIdStore)))
                services.AddSingleton<IAppliedTsIdStore>(_ => new FileAppliedTsIdStore(options.RecoveryJournalDirectory!));
        }

        services.AddSingleton<HihooiTransactionManager>();
        services.AddSingleton<HihooiClient>();
        services.AddSingleton<HihooiDatabase>();
        services.AddSingleton<IHihooiDatabase>(sp => sp.GetRequiredService<HihooiDatabase>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, HihooiRuntimeHostedService>());
        return services;
    }
}
