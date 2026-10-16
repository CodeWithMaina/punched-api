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
        // Optional so unit-test service providers without guards keep working.
        var guard = provider.GetService<INotificationDispatchGuard>();
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

                // Unregistered mid-flight type: terminal immediately (classified as a
                // permanent dead-letter) rather than throwing in BuildMessage forever.
                if (!NotificationTypes.TryGet(row.TemplateType, out var definition))
                {
                    MarkFailed(row, $"permanent:unregistered_type:{row.TemplateType}");
                    await context.SaveChangesAsync(ct);
                    NotificationMetrics.RecordFailed(row.Channel);
                    NotificationMetrics.RecordDeadLettered(row.Channel, "permanent");
                    _logger.LogInformation(
                        "Notification delivery dead-lettered {OutboxId} on channel {Channel}: unregistered type {TemplateType}",
                        row.Id, row.Channel, row.TemplateType);
                    continue;
                }

                var channel = Array.Find(channels, candidate =>
                    string.Equals(candidate.Name, row.Channel, StringComparison.Ordinal));

                if (channel is not null && guard is not null)
                {
                    // Phase 1.6 dispatch guard: suppression / frequency cap / quiet
                    // hours / tenant budget. Denials DEFER the row without consuming
                    // a delivery attempt — deferral, never destruction.
                    var verdict = await guard.EvaluateAsync(row, ct);
                    if (!verdict.Allow)
                    {
                        row.Status = "pending";
                        row.NextAttemptAt = verdict.RetryAtUtc ?? DateTime.UtcNow.AddHours(1);
                        row.UpdatedAt = DateTime.UtcNow;
                        await context.SaveChangesAsync(ct);
                        NotificationMetrics.RecordSuppressed(definition.Category, verdict.Reason ?? "dispatch_guard");
                        _logger.LogInformation(
                            "Notification delivery deferred {OutboxId} on {Channel} for {TemplateType} until {RetryAtUtc}: {DeferralReason}",
                            row.Id, row.Channel, row.TemplateType, row.NextAttemptAt, verdict.Reason);
                        continue;
                    }
                }

                if (channel is null)
                {
                    MarkFailed(row, "permanent:no_channel_registered");
                    await context.SaveChangesAsync(ct);
                    NotificationMetrics.RecordFailed(row.Channel);
                    NotificationMetrics.RecordDeadLettered(row.Channel, "permanent");
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
                NotificationMetrics.RecordDeadLettered(row.Channel, "permanent");
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

                // Per-type retry profile from the registry (Phase 1.4): attempts and
                // exponential backoff come from the type definition — e.g.
                // security.verification_code retries 10s → 60s for 3 attempts — with
                // the legacy 3-attempt / 30s→5min shape as the fallback.
                NotificationTypeDef? retryDefinition = null;
                if (NotificationTypes.TryGet(row.TemplateType, out var retryDef))
                    retryDefinition = retryDef;

                var maxAttempts = retryDefinition?.RetryAttempts ?? 3;
                if (row.Attempts >= maxAttempts)
                {
                    MarkFailed(row, $"retry_exhausted:{ex.Message}");
                    await context.SaveChangesAsync(ct);
                    NotificationMetrics.RecordFailed(row.Channel);
                    NotificationMetrics.RecordDeadLettered(row.Channel, "retry_exhausted");
                    _logger.LogInformation(
                        "Notification delivery terminal {OutboxId} on channel {Channel} for {TemplateType}: retries exhausted after {Attempts} attempts in {DurationMs} ms",
                        row.Id, row.Channel, row.TemplateType, row.Attempts, deliveryDurationMs);
                }
                else
                {
                    var backoff = retryDefinition?.InitialRetryDelay ?? TimeSpan.FromSeconds(30);
                    var maxBackoff = retryDefinition?.MaxRetryDelay ?? TimeSpan.FromMinutes(5);
                    for (var i = 1; i < row.Attempts; i++)
                    {
                        backoff = TimeSpan.FromTicks(backoff.Ticks * 2);
                        if (backoff >= maxBackoff) break;
                    }
                    if (backoff > maxBackoff) backoff = maxBackoff;

                    row.Status = "pending";
                    row.NextAttemptAt = row.UpdatedAt.Add(backoff);
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

        // Terminal rows carry the dead-letter classification (Phase 1.7): the admin
        // failures view derives the same classes from the error prefix, and replay
        // clears both fields when it requeues the row.
        row.DeadLetteredAt = DateTime.UtcNow;
        row.DeadLetterReason = error.StartsWith("retry_exhausted:", StringComparison.Ordinal)
            ? "retry_exhausted"
            : error.StartsWith("permanent:", StringComparison.Ordinal)
                ? "permanent"
                : "unknown";
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
