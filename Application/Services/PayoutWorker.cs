using PunchedApi.Application.BackgroundProcessing;
using PunchedApi.Domain.Interfaces;

namespace PunchedApi.Application.Services;

/// <summary>
/// Processes due payout redemptions.
///
/// <para>Formerly a fixed 30-second loop; now a periodic sweep that jitters its
/// interval and, in scale-to-zero-friendly mode, runs on the shared background tick.
/// The sweep itself is idempotent, so a longer interval changes latency, never
/// correctness.</para>
/// </summary>
public sealed class PayoutWorker : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PayoutWorker> _logger;
    private readonly PollingProfile? _profile;
    private readonly BackgroundProcessingMetrics _metrics;

    public PayoutWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<PayoutWorker> logger,
        BackgroundProcessingOptions? backgroundProcessing = null,
        BackgroundProcessingMetrics? metrics = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _profile = backgroundProcessing?.For(BackgroundProcessingOptions.Names.Payouts);
        _metrics = metrics ?? BackgroundProcessingMetrics.Shared;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        PeriodicWorkerLoop.RunAsync(
            BackgroundProcessingOptions.Names.Payouts,
            _profile ?? new PollingProfile(),
            Interval,
            _logger,
            _metrics,
            async ct =>
            {
                var startedAt = DateTime.UtcNow;
                await using var scope = _scopeFactory.CreateAsyncScope();
                var payoutService = scope.ServiceProvider.GetRequiredService<IPayoutService>();

                var processed = await payoutService.ProcessDueRedemptionsAsync(ct);

                if (processed > 0)
                {
                    _logger.LogInformation(
                        "PayoutWorker cycle complete. Processed={Processed}, DurationMs={DurationMs}",
                        processed,
                        (int)(DateTime.UtcNow - startedAt).TotalMilliseconds);
                }

                return processed;
            },
            stoppingToken);
}
