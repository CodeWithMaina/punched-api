using PunchedApi.Application.BackgroundProcessing;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

/// <summary>
/// Periodic background worker (default every 24h, configurable via
/// "Workers:SubscriptionExpiryIntervalHours") that expires overdue subscriptions so
/// module access lapses automatically.
///
/// <para>Runs through <see cref="PeriodicWorkerLoop"/>, so in scale-to-zero-friendly
/// mode it joins the shared daily tick instead of waking the database on its own.</para>
/// </summary>
public sealed class SubscriptionExpiryWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SubscriptionExpiryWorker> _logger;
    private readonly TimeSpan _interval;
    private readonly PollingProfile? _profile;
    private readonly BackgroundProcessingMetrics _metrics;

    public SubscriptionExpiryWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<SubscriptionExpiryWorker> logger,
        IConfiguration configuration,
        BackgroundProcessingOptions? backgroundProcessing = null,
        BackgroundProcessingMetrics? metrics = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        var hours = configuration.GetValue<double?>("Workers:SubscriptionExpiryIntervalHours") ?? 24;
        _interval = TimeSpan.FromHours(Math.Max(hours, 0.1));
        _profile = backgroundProcessing?.For(BackgroundProcessingOptions.Names.SubscriptionExpiry);
        _metrics = metrics ?? BackgroundProcessingMetrics.Shared;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        PeriodicWorkerLoop.RunAsync(
            BackgroundProcessingOptions.Names.SubscriptionExpiry,
            _profile ?? new PollingProfile(),
            _interval,
            _logger,
            _metrics,
            async ct =>
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var expiryService = scope.ServiceProvider.GetRequiredService<SubscriptionExpiryService>();
                var expired = await expiryService.ExpireOverdueAsync(DateTime.UtcNow, ct);
                if (expired > 0)
                {
                    _logger.LogInformation("SubscriptionExpiryWorker expired {Count} business(es).", expired);
                }
                return expired;
            },
            stoppingToken);
}
