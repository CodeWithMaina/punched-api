using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PunchedApi.Application.BackgroundProcessing;
using PunchedApi.Application.Notifications;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Infrastructure.Notifications;

/// <summary>
/// Drains the notification ledger into the external channels, with adaptive polling.
///
/// <para>The durable state machine is unchanged: rows are still claimed atomically
/// with <c>FOR UPDATE SKIP LOCKED</c>, leases are still reclaimed, retries still back
/// off and terminal failures still dead-letter. Only the <i>waiting</i> changed — the
/// fixed sleep became a bounded, jittered backoff that can reach deep idle so the
/// database may suspend, and an outbox commit wakes the worker immediately so deep
/// idle costs no delivery latency.</para>
/// </summary>
public sealed class NotificationDeliveryWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<NotificationDeliveryWorker> _logger;
    private readonly NotificationWorkerOptions _options;
    private readonly PollingProfile? _profile;
    private readonly BackgroundProcessingMetrics _metrics;
    private readonly IPollWakeChannel? _wakeChannel;
    private readonly int _batchSize;

    public NotificationDeliveryWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<NotificationDeliveryWorker> logger,
        IOptions<NotificationWorkerOptions>? options = null,
        BackgroundProcessingOptions? backgroundProcessing = null,
        BackgroundProcessingMetrics? metrics = null,
        IPollWakeChannel? wakeChannel = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options?.Value ?? new NotificationWorkerOptions();
        _profile = backgroundProcessing?.For(BackgroundProcessingOptions.Names.Notifications);
        _metrics = metrics ?? BackgroundProcessingMetrics.Shared;
        _wakeChannel = wakeChannel;
        _batchSize = Math.Max(1, _profile?.BatchSize ?? _options.BatchSize);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        AdaptivePollLoop.RunAsync(
            BackgroundProcessingOptions.Names.Notifications,
            ResolveProfile(),
            _logger,
            _metrics,
            async ct =>
            {
                var processed = await ProcessBatchAsync(ct);
                return new PollWorkResult(processed, processed, processed >= _batchSize);
            },
            _wakeChannel,
            stoppingToken);

    /// <summary>
    /// The configured profile, or — when this worker is constructed by hand (tests,
    /// tooling) without the new configuration — a profile that reproduces the old
    /// fixed-interval behaviour exactly.
    /// </summary>
    private PollingProfile ResolveProfile()
    {
        if (_profile is not null) return _profile;

        var seconds = Math.Max(1, _options.PollIntervalSeconds);
        return new PollingProfile
        {
            ActiveIntervalSeconds = Math.Max(1, Math.Min(2, seconds)),
            FirstIdleIntervalSeconds = seconds,
            MaxIdleIntervalSeconds = seconds,
            BackoffMultiplier = 1,
            JitterRatio = 0,
            MaxIdleDurationSeconds = 0
        };
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
        // Optional so unit-test service providers without the resolver keep working
        // (they fall back to the platform default in BuildMessageAsync).
        var resolver = provider.GetService<ITemplateOverrideResolver>();
        var batchSize = _batchSize;

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

                var channelMessage = await BuildMessageAsync(row, renderer, resolver, ct);
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

        // One round trip, two numbers: the backlog and how old its oldest row is.
        // The age is the honest backlog signal — a count of 1 that has waited an hour
        // is worse than a count of 200 that arrived a second ago — and grouping keeps
        // it free of an extra query per batch.
        var pending = await context.NotificationLogs
            .Where(row => row.Status == "pending")
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Count = group.Count(),
                OldestCreatedAt = group.Min(row => row.CreatedAt)
            })
            .FirstOrDefaultAsync(ct);

        var pendingRows = pending?.Count ?? 0;
        NotificationMetrics.RecordQueueDepth(pendingRows);

        if (pending is not null && pendingRows > 0)
        {
            var oldestAgeSeconds = Math.Max(0, (DateTime.UtcNow - pending.OldestCreatedAt).TotalSeconds);
            NotificationMetrics.RecordOldestPendingAge(oldestAgeSeconds);
        }

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

    private async Task<ChannelMessage> BuildMessageAsync(
        NotificationLog row,
        ITemplateRenderer renderer,
        ITemplateOverrideResolver? resolver,
        CancellationToken ct)
    {
        var data = DeserializePayload(row.PayloadJson);

        // Effective template: business override wins, platform default is the
        // safe fallback. An unregistered type was already dead-lettered before
        // BuildMessageAsync is called, so ResolveAsync will always return the
        // platform default at minimum — never a silent bypass.
        EffectiveTemplate effective;
        if (resolver is not null)
        {
            effective = await resolver.ResolveAsync(row.BusinessId, row.TemplateType, row.Channel, ct)
                ?? throw new InvalidOperationException(
                    $"No template resolvable for {row.TemplateType}/{row.Channel}.");
        }
        else
        {
            // No resolver registered (e.g. unit-test service providers): fall back
            // to the platform default template.
            var fallback = NotificationTemplates.Get(row.TemplateType, row.Channel);
            effective = new EffectiveTemplate(fallback.Subject, fallback.Title, fallback.Body, fallback.Body, EffectiveTemplateSource.Default);
        }

        var definition = NotificationTypes.Get(row.TemplateType);

        data["renderedSubject"] = renderer.RenderText(effective.Subject ?? string.Empty, data);
        data["renderedTitle"] = renderer.RenderText(effective.Title, data);
        data["renderedBody"] = renderer.RenderText(effective.Body, data);
        data["renderedHtmlBody"] = renderer.RenderHtml(effective.BodyHtml, data);

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
