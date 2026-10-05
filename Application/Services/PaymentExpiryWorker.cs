using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PunchedApi.Application.Services;

public class PaymentExpiryWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PaymentExpiryWorker> _logger;
    private readonly TimeSpan _interval;

    public PaymentExpiryWorker(IServiceScopeFactory scopeFactory, ILogger<PaymentExpiryWorker> logger, IConfiguration configuration)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        // STK prompts die on their own; a frequent short sweep keeps Punched state honest.
        // Configurable via "Workers:PaymentExpiryIntervalMinutes" (default 1 minute).
        var minutes = configuration.GetValue<double?>("Workers:PaymentExpiryIntervalMinutes") ?? 1;
        _interval = TimeSpan.FromMinutes(Math.Max(minutes, 0.25));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PaymentExpiryWorker started (interval {Interval}).", _interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var payments = scope.ServiceProvider.GetRequiredService<IPaymentService>();
                var expired = await payments.ExpireStalePaymentsAsync();
                if (expired > 0)
                    _logger.LogInformation("PaymentExpiryWorker expired {Count} stale payment(s).", expired);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "PaymentExpiryWorker cycle failed.");
            }

            await Task.Delay(_interval, stoppingToken);
        }

        _logger.LogInformation("PaymentExpiryWorker stopped.");
    }
}