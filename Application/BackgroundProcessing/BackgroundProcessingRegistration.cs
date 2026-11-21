using Microsoft.Extensions.Options;
using PunchedApi.Infrastructure.Caching;

namespace PunchedApi.Application.BackgroundProcessing;

/// <summary>
/// Registers the shared background-processing services:
/// the per-worker polling profiles, the metrics meter, the wake-up channel and the
/// SaveChanges interceptor that raises wake-ups when durable work is committed.
/// </summary>
public static class BackgroundProcessingRegistration
{
    public static IServiceCollection AddBackgroundProcessing(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton<BackgroundProcessingOptions>();
        services.AddSingleton<BackgroundProcessingMetrics>();
        services.AddSingleton<LocalPollWakeChannel>();

        services.AddSingleton<IPollWakeChannel>(sp =>
        {
            var local = sp.GetRequiredService<LocalPollWakeChannel>();
            var multiplexer = sp.GetService<StackExchange.Redis.IConnectionMultiplexer>();

            // No Redis (local development, tests, Redis disabled): the in-process
            // channel is still correct for a single container.
            if (multiplexer is null) return local;

            var settings = sp.GetService<IOptions<RedisSettings>>()?.Value ?? new RedisSettings();
            var logger = sp.GetRequiredService<ILogger<RedisPollWakeChannel>>();
            return new RedisPollWakeChannel(multiplexer, settings, local, logger);
        });

        return services;
    }

    /// <summary>
    /// Validates every resolved profile and throws with the complete list of problems.
    /// Called at startup so a bad deployment never reaches the point where it silently
    /// busy-polls (and burns the compute allowance) or never polls at all.
    /// </summary>
    public static void ValidateBackgroundProcessing(this IServiceProvider services)
    {
        var options = services.GetRequiredService<BackgroundProcessingOptions>();
        var problems = options.Validate();

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "Invalid BackgroundProcessing configuration:" +
                Environment.NewLine +
                string.Join(Environment.NewLine, problems.Select(problem => " - " + problem)));
        }
    }
}
