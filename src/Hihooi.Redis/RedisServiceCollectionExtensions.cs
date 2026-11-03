using Hihooi.Core.Abstractions;
using Hihooi.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Hihooi.Redis;

public static class RedisServiceCollectionExtensions
{
    public static IServiceCollection AddHihooiRedisBuffer(this IServiceCollection services, string connectionString, string keyPrefix = "hihooi:")
    {
        services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(connectionString));
        services.AddSingleton<ITransactionsBuffer>(sp =>
        {
            var mux = sp.GetRequiredService<IConnectionMultiplexer>();
            return new RedisTransactionsBuffer(mux, keyPrefix);
        });
        return services;
    }

    public static IServiceCollection AddHihooiRedisBuffer(this IServiceCollection services, HihooiOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.RedisConnectionString))
            throw new InvalidOperationException("HihooiOptions.RedisConnectionString is required for Redis buffer.");
        return services.AddHihooiRedisBuffer(options.RedisConnectionString, options.RedisKeyPrefix);
    }
}
