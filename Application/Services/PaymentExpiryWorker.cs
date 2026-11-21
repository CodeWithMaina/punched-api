using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PunchedApi.Application.BackgroundProcessing;

namespace PunchedApi.Application.Services;

/// <summary>
/// Expires stale payment attempts and abandoned manual M-PESA submissions.
///
/// <para>Time-based sweep: the interval is <c>Workers:PaymentExpiryIntervalMinutes</c>
/// (default 1 minute). In scale-to-zero-friendly mode the sweep is folded onto the
/// shared background tick so it does not keep the database awake on its own; the
/// effective period is logged at startup.</para>
/// </summary>
public class PaymentExpiryWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PaymentExpiryWorker> _logger;
    private readonly TimeSpan _interval;
    private readonly PollingProfile? _profile;
    private readonly BackgroundProcessingMetrics _metrics;

    public PaymentExpiryWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<PaymentExpiryWorker> logger,
        IConfiguration configuration,
        BackgroundProcessingOptions? backgroundProcessing = null,
        BackgroundProcessingMetrics? metrics = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        // STK prompts die on their own; a short sweep keeps Punched state honest.
        // Configurable via "Workers:PaymentExpiryIntervalMinutes" (default 1 minute).
        var minutes = configuration.GetValue<double?>("Workers:PaymentExpiryIntervalMinutes") ?? 1;
        _interval = TimeSpan.FromMinutes(Math.Max(minutes, 0.25));
        _profile = backgroundProcessing?.For(BackgroundProcessingOptions.Names.PaymentExpiry);
        _metrics = metrics ?? BackgroundProcessingMetrics.Shared;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        PeriodicWorkerLoop.RunAsync(
            BackgroundProcessingOptions.Names.PaymentExpiry,
            _profile ?? new PollingProfile(),
            _interval,
            _logger,
            _metrics,
            async ct =>
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var payments = scope.ServiceProvider.GetRequiredService<IPaymentService>();

                var expired = await payments.ExpireStalePaymentsAsync();
                if (expired > 0)
                    _logger.LogInformation("PaymentExpiryWorker expired {Count} stale payment(s).", expired);

                // Manual M-PESA submissions nobody decided. Same sweep, different clock:
                // the window is days, not minutes, so this is normally a no-op, but a
                // booking whose fee is never verified must not hold its slot forever.
                var lapsed = await payments.ExpireStaleVerificationsAsync();
                if (lapsed > 0)
                    _logger.LogInformation("PaymentExpiryWorker lapsed {Count} unverified payment submission(s).", lapsed);

                return expired + lapsed;
            },
            stoppingToken);
}
