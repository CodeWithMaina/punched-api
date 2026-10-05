using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PunchedApi.Application.Notifications;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Infrastructure.Notifications;

/// <summary>Polls the existing notification ledger and invokes external channels.</summary>
public sealed class NotificationDeliveryWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<NotificationDeliveryWorker> _logger;
    private readonly NotificationWorkerOptions _options;

    public NotificationDeliveryWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<NotificationDeliveryWorker> logger,
        IOptions<NotificationWorkerOptions>? options = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options?.Value ?? new NotificationWorkerOptions();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pollInterval = TimeSpan.FromSeconds(Math.Max(1, _options.PollIntervalSeconds));
        _logger.LogInformation("Notification delivery worker started. Poll interval: {PollInterval}", pollInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Notification delivery worker failed to process an outbox batch.");
            }

            await Task.Delay(pollInterval, stoppingToken);
        }

        _logger.LogInformation("Notification delivery worker stopped.");
    }

    /// <summary>Runs one independently scoped batch; public for deterministic worker tests.</summary>
    public async Task<int> ProcessBatchAsync(CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var store = provider.GetRequiredService<NotificationOutboxStore>();
        var context = provider.GetRequiredService<ApplicationDbContext>();
        var renderer = provider.GetRequiredService<ITemplateRenderer>();
        var channels = provider.GetServices<INotificationChannel>().ToArray();
        var batchSize = Math.Max(1, _options.BatchSize);

        await store.ReclaimStaleProcessingAsync(TimeSpan.FromMinutes(Math.Max(1, _options.StaleProcessingMinutes)), ct);
        var claimed = await store.ClaimAsync(batchSize, ct);

        foreach (var claimedRow in claimed)
        {
            ct.ThrowIfCancellationRequested();
            var deliveryDurationMs = 0d;
            NotificationDeliveryAttemptHistory? attemptHistory = null;
            NotificationDeliveryAttempt? currentAttempt = null;
            try
            {
                var row = await context.NotificationLogs.SingleAsync(item => item.Id == claimedRow.Id, ct);
                var channel = Array.Find(channels, candidate =>
                    string.Equals(candidate.Name, row.Channel, StringComparison.Ordinal));

                if (channel is null)
                {
                    MarkFailed(row, "permanent:no_channel_registered");
                    await context.SaveChangesAsync(ct);
                    NotificationMetrics.RecordFailed(row.Channel);
                    _logger.LogInformation(
                        "Notification delivery terminal {OutboxId} on channel {Channel} for {TemplateType}: no_channel_registered after {Attempts} attempts in {DurationMs} ms",
                        row.Id, row.Channel, row.TemplateType, row.Attempts, deliveryDurationMs);
                    continue;
                }

                var channelMessage = BuildMessage(row, renderer);
                attemptHistory = NotificationDeliveryAttemptHistory.Deserialize(row.DeliveryAttemptsJson);
                currentAttempt = attemptHistory.Start(DateTime.UtcNow);
                row.DeliveryAttemptsJson = attemptHistory.Serialize();
                row.UpdatedAt = currentAttempt.StartedAtUtc;
                await context.SaveChangesAsync(ct);

                var deliveryTimer = Stopwatch.StartNew();
                try
                {
                    await channel.SendAsync(channelMessage, ct);
                }
                finally
                {
                    deliveryDurationMs = deliveryTimer.Elapsed.TotalMilliseconds;
                    NotificationMetrics.RecordDeliveryDuration(row.Channel, deliveryDurationMs);
                }

                var now = DateTime.UtcNow;
                CompleteAttempt(currentAttempt, "sent", now);
                row.Status = "sent";
                row.SentAt = now;
                row.UpdatedAt = now;
                row.Error = null;
                row.DeliveryAttemptsJson = attemptHistory.Serialize();
                await context.SaveChangesAsync(ct);
                NotificationMetrics.RecordSent(row.Channel);
                _logger.LogInformation(
                    "Notification delivery terminal {OutboxId} on channel {Channel} for {TemplateType}: sent after {Attempts} attempts in {DurationMs} ms",
                    row.Id, row.Channel, row.TemplateType, row.Attempts + 1, deliveryDurationMs);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (PermanentChannelException ex)
            {
                var row = await context.NotificationLogs.SingleAsync(item => item.Id == claimedRow.Id, ct);
                CompleteAttempt(currentAttempt, "permanent_failure", DateTime.UtcNow);
                if (attemptHistory is not null)
                    row.DeliveryAttemptsJson = attemptHistory.Serialize();
                MarkFailed(row, $"permanent:{ex.Message}");
                await context.SaveChangesAsync(ct);
                NotificationMetrics.RecordFailed(row.Channel);
                _logger.LogInformation(
                    "Notification delivery terminal {OutboxId} on channel {Channel} for {TemplateType}: permanent failure after {Attempts} attempts in {DurationMs} ms",
                    row.Id, row.Channel, row.TemplateType, row.Attempts + 1, deliveryDurationMs);
            }
            catch (Exception ex)
            {
                ct.ThrowIfCancellationRequested();
                var row = await context.NotificationLogs.SingleAsync(item => item.Id == claimedRow.Id, ct);
                CompleteAttempt(currentAttempt, "retryable_failure", DateTime.UtcNow);
                if (attemptHistory is not null)
                    row.DeliveryAttemptsJson = attemptHistory.Serialize();
                row.Attempts++;
                row.UpdatedAt = DateTime.UtcNow;
                if (row.Attempts >= 3)
                {
                    MarkFailed(row, $"retry_exhausted:{ex.Message}");
                    await context.SaveChangesAsync(ct);
                    NotificationMetrics.RecordFailed(row.Channel);
                    _logger.LogInformation(
                        "Notification delivery terminal {OutboxId} on channel {Channel} for {TemplateType}: retries exhausted after {Attempts} attempts in {DurationMs} ms",
                        row.Id, row.Channel, row.TemplateType, row.Attempts, deliveryDurationMs);
                }
                else
                {
                    row.Status = "pending";
                    row.NextAttemptAt = row.UpdatedAt.Add(row.Attempts == 1
                        ? TimeSpan.FromSeconds(30)
                        : TimeSpan.FromMinutes(5));
                    await context.SaveChangesAsync(ct);
                    _logger.LogWarning(
                        "Notification delivery retry {OutboxId} on channel {Channel} for {TemplateType}; attempt {Attempts} took {DurationMs} ms and is scheduled for {NextAttemptAt}",
                        row.Id, row.Channel, row.TemplateType, row.Attempts, deliveryDurationMs, row.NextAttemptAt);
                }
            }
        }

        var pendingRows = await context.NotificationLogs.CountAsync(row => row.Status == "pending", ct);
        NotificationMetrics.RecordQueueDepth(pendingRows);
        return claimed.Count;
    }

    private static void MarkFailed(NotificationLog row, string error)
    {
        row.Status = "failed";
        row.Error = error.Length <= 500 ? error : error[..500];
        row.UpdatedAt = DateTime.UtcNow;
    }

    private static void CompleteAttempt(
        NotificationDeliveryAttempt? attempt,
        string outcome,
        DateTime completedAtUtc)
    {
        if (attempt is null) return;
        attempt.Outcome = outcome;
        attempt.CompletedAtUtc = completedAtUtc;
    }

    private static ChannelMessage BuildMessage(NotificationLog row, ITemplateRenderer renderer)
    {
        var data = DeserializePayload(row.PayloadJson);
        var definition = NotificationTypes.Get(row.TemplateType);
        var template = NotificationTemplates.Get(row.TemplateType, row.Channel);
        data["renderedSubject"] = renderer.RenderText(template.Subject, data);
        data["renderedTitle"] = renderer.RenderText(template.Title, data);
        data["renderedBody"] = renderer.RenderText(template.Body, data);
        data["renderedHtmlBody"] = renderer.RenderHtml(template.Body, data);
        return new ChannelMessage(
            row.Id,
            row.TemplateType,
            row.UserId,
            row.BusinessId,
            definition.Category,
            data);
    }

    private static Dictionary<string, object?> DeserializePayload(string payload)
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(payload) ?? new();
        return values.ToDictionary(pair => pair.Key, pair => (object?)pair.Value);
    }
}
