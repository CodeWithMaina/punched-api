using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace PunchedApi.Infrastructure.Caching;

public static class CacheServiceCollectionExtensions
{
    /// <summary>
    /// Registers the cache-aside store: Redis when <c>Redis:Enabled=true</c>, an
    /// in-process memory cache otherwise. Both satisfy <see cref="ICacheStore"/>,
    /// so no feature has to branch on which one is active.
    /// </summary>
    public static IServiceCollection AddPunchedCache(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<RedisSettings>(configuration.GetSection(RedisSettings.SectionName));

        var settings = configuration.GetSection(RedisSettings.SectionName).Get<RedisSettings>()
            ?? new RedisSettings();

        if (!settings.Enabled)
        {
            services.AddSingleton<ICacheStore, MemoryCacheStore>();
            return services;
        }

        // Refusing to start is better than booting with a Redis layer that can
        // never connect: the operator asked for it, so it must be configured.
        if (string.IsNullOrWhiteSpace(settings.ConnectionString))
        {
            throw new InvalidOperationException(
                "Redis:Enabled is true but Redis:ConnectionString is empty. Set " +
                "Redis__ConnectionString (redis:6379,password=… in the compose stack) or set " +
                "Redis__Enabled=false to run memory-only.");
        }

        if (!settings.ConnectionString.Contains("password=", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Redis:ConnectionString has no password. The Redis container requires authentication " +
                "(--requirepass) and must never be reachable unauthenticated.");
        }

        services.AddSingleton<IConnectionMultiplexer>(_ =>
        {
            var options = ConfigurationOptions.Parse(settings.ConnectionString);

            // Never abort startup because Redis is down: the cache degrades to a
            // miss, and readiness keeps reporting the true state. Bounded
            // timeouts keep a wedged Redis off the request path.
            options.AbortOnConnectFail = false;
            options.ConnectTimeout = 2000;
            options.SyncTimeout = 2000;
            options.AsyncTimeout = 2000;
            options.ConnectRetry = 2;
            options.DefaultDatabase = 0;
            options.AllowAdmin = false;
            options.ClientName = "punched-api";

            return ConnectionMultiplexer.Connect(options);
        });

        services.AddSingleton<ICacheStore, RedisCacheStore>();
        return services;
    }
}
