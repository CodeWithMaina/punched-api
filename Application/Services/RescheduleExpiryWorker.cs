using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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

    public RescheduleExpiryWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<RescheduleExpiryWorker> logger,
        IConfiguration configuration)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        var minutes = configuration.GetValue<double?>("Workers:RescheduleExpiryIntervalMinutes") ?? 15;
        _interval = TimeSpan.FromMinutes(Math.Max(minutes, 1));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("RescheduleExpiryWorker started (interval {Interval}).", _interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var appointments = scope.ServiceProvider.GetRequiredService<IAppointmentService>();
                var expired = await appointments.ExpireStaleRescheduleRequestsAsync(stoppingToken);
                if (expired > 0)
                    _logger.LogInformation(
                        "RescheduleExpiryWorker expired {Count} stale reschedule request(s).", expired);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "RescheduleExpiryWorker cycle failed.");
            }

            try
            {
                await Task.Delay(_interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("RescheduleExpiryWorker stopped.");
    }
}
