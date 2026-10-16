using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Notifications;

/// <summary>
/// Transactional notification-intent queue backed by <see cref="NotificationLog"/>.
/// The row is created and committed in the caller's unit of work — there is no
/// separate commit here. Produces are idempotent-prefixed via
/// <see cref="NotificationLog.IdempotencyKey"/> at the call site.
/// </summary>
public sealed class NotificationOutboxService : INotificationOutboxService
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<NotificationOutboxService> _logger;

    public NotificationOutboxService(ApplicationDbContext context, ILogger<NotificationOutboxService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public Task<NotificationLog> QueueAsync(
        Guid userId,
        Guid? businessId,
        string templateType,
        string channel,
        string templateKey,
        IReadOnlyDictionary<string, object?> payload,
        DateTime? deliverAfterUtc = null,
        string? ruleId = null,
        string? sourceJobId = null,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var nowUtc = now;

        var row = new NotificationLog
        {
            UserId = userId,
            BusinessId = businessId,
            Channel = channel,
            TemplateType = templateType,
            TemplateKey = templateKey,
            Status = "pending",
            SentAt = nowUtc,
            NextAttemptAt = nowUtc,
            DeliveredAt = null,
            OpenedAt = null,
            PayloadJson = payload != null ? JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false }) : "{}",
            Attempts = 0,
            DeliveryAttemptsJson = null,
            DeliverAfterUtc = deliverAfterUtc,
            RuleId = ruleId,
            SourceJobId = sourceJobId,
            DeadLetteredAt = null,
            DeadLetterReason = null,
            IdempotencyKey = null,
            Error = null,
            CreatedAt = nowUtc,
            UpdatedAt = nowUtc
        };

        _context.NotificationLogs.Add(row);
        // No SaveChanges here — the caller owns the unit of work and commits this
        // row atomically with the domain change.
        _logger.LogDebug(
            "Queued notification intent {OutboxId} (type={Type}, channel={Channel}, deliverAfterUtc={DeliverAfterUtc}, ruleId={RuleId})",
            row.Id, templateType, channel, deliverAfterUtc, ruleId);
        return Task.FromResult(row);
    }

    public async Task MarkSentAsync(NotificationLog row, DateTime deliveredAtUtc, CancellationToken ct = default)
    {
        row.DeliveredAt = deliveredAtUtc;
        row.Status = "sent";
        row.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);
        _logger.LogDebug("Notification intent {OutboxId} marked sent at {DeliveredAtUtc}", row.Id, deliveredAtUtc);
    }

    public async Task StartProcessingAsync(NotificationLog row, CancellationToken ct = default)
    {
        row.Status = "processing";
        row.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);
        _logger.LogDebug("Notification intent {OutboxId} claimed for processing", row.Id);
    }
}
