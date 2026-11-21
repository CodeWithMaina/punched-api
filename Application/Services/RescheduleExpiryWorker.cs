using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PunchedApi.Application.BackgroundProcessing;
using PunchedApi.Domain.Interfaces;

namespace PunchedApi.Application.Services;

/// <summary>
/// Moves undecided reschedule requests past their expiry into the <c>expired</c>
/// terminal state, so a proposal can never sit actionable forever and hold a slot
/// indefinitely.
///
/// <para>The sweep itself is idempotent (it only selects <c>pending</c> rows whose
/// <c>ExpiresAt</c> has passed), so overlapping cycles or multiple application
/// instances cannot expire the same request twice or double-notify.</para>
///
/// <para>Interval: <c>Workers:RescheduleExpiryIntervalMinutes</c>, default 15.</para>
/// </summary>
public class RescheduleExpiryWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RescheduleExpiryWorker> _logger;
    private readonly TimeSpan _interval;
    private readonly PollingProfile? _profile;
    private readonly BackgroundProcessingMetrics _metrics;

    public RescheduleExpiryWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<RescheduleExpiryWorker> logger,
        IConfiguration configuration,
        BackgroundProcessingOptions? backgroundProcessing = null,
        BackgroundProcessingMetrics? metrics = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        var minutes = configuration.GetValue<double?>("Workers:RescheduleExpiryIntervalMinutes") ?? 15;
        _interval = TimeSpan.FromMinutes(Math.Max(minutes, 1));
        _profile = backgroundProcessing?.For(BackgroundProcessingOptions.Names.RescheduleExpiry);
        _metrics = metrics ?? BackgroundProcessingMetrics.Shared;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        PeriodicWorkerLoop.RunAsync(
            BackgroundProcessingOptions.Names.RescheduleExpiry,
            _profile ?? new PollingProfile(),
            _interval,
            _logger,
            _metrics,
            async ct =>
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var appointments = scope.ServiceProvider.GetRequiredService<IAppointmentService>();
                var expired = await appointments.ExpireStaleRescheduleRequestsAsync(ct);
                if (expired > 0)
                    _logger.LogInformation(
                        "RescheduleExpiryWorker expired {Count} stale reschedule request(s).", expired);
                return expired;
            },
            stoppingToken);
}
