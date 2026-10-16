using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Notifications;

/// <summary>
/// The notification core. Producers hand it an intent; it resolves preferences,
/// writes the in-app inbox row (with its ledger row, same unit of work — fixes the
/// audit's B4/B7) and queues external channels on the <c>notifications</c> outbox.
/// <see cref="SendAsync"/> commits those rows itself; <see cref="QueueAsync"/> hands
/// them to the caller's <c>SaveChanges</c> so the intent commits atomically with the
/// domain change that produced it.
/// </summary>
/// <remarks>
/// Provider-agnostic by construction: it knows nothing about SMTP, SMS vendors,
/// VAPID or how preferences resolve internally.
/// </remarks>
public sealed class NotificationService : INotificationService
{
    /// <summary>Reason returned when no candidate channel survived preferences.</summary>
    public const string SuppressedByPreferences = "suppressed_by_preferences";

    private readonly IUnitOfWork _unitOfWork;
    private readonly ApplicationDbContext _context;
    private readonly IPreferenceResolver _preferences;
    private readonly ILogger<NotificationService> _logger;

    /// <summary>Dispatch guard (suppression etc.); null only in unit tests.</summary>
    private readonly INotificationDispatchGuard? _dispatchGuard;

    public NotificationService(
        IUnitOfWork unitOfWork,
        ApplicationDbContext context,
        IPreferenceResolver preferences,
        ILogger<NotificationService> logger,
        INotificationDispatchGuard? dispatchGuard = null)
    {
        _unitOfWork = unitOfWork;
        _context = context;
        _preferences = preferences;
        _logger = logger;
        _dispatchGuard = dispatchGuard;
    }

    /// <inheritdoc />
    public Task<NotificationResult> SendAsync(NotificationRequest request, CancellationToken ct = default)
        => AcceptAsync(request, persistInline: true, ct);

    /// <inheritdoc />
    public Task<NotificationResult> QueueAsync(NotificationRequest request, CancellationToken ct = default)
        => AcceptAsync(request, persistInline: false, ct);

    /// <summary>
    /// Shared acceptance pipeline (registry → tenant guard → preferences → in-app write
    /// → external outbox queue). With <paramref name="persistInline"/> each stage commits
    /// its own rows here (fire-and-forget <see cref="SendAsync"/>); without it every row
    /// stays tracked in the caller's unit of work so the caller's single SaveChanges
    /// commits intent and domain change together (<see cref="QueueAsync"/> — Phase 1
    /// intent-level outbox).
    /// </summary>
    private async Task<NotificationResult> AcceptAsync(NotificationRequest request, bool persistInline, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Data);

        // 1. Registry lookup — an unknown type is a programmer error and throws
        //    rather than rendering caller-supplied strings (architecture doc §14.4).
        var definition = NotificationTypes.Get(request.Type);

        // 2. Tenant guard — the tenant comes from the caller's context, never from
        //    client input; a mismatched recipient writes nothing.
        if (request.BusinessId.HasValue)
            await EnsureRecipientBelongsToBusinessAsync(request.RecipientUserId, request.BusinessId.Value, ct);

        // 3. Preferences. R2 — only SECURITY may bypass them.
        var surviving = request.Force && definition.Category == NotificationCategory.Security
            ? (IReadOnlyList<string>)ForcePastPreferences(request, definition)
            : await _preferences.ResolveAsync(
                request.RecipientUserId, request.BusinessId, definition.Category, definition.DefaultChannels, ct);

        if (surviving.Count == 0)
        {
            NotificationMetrics.RecordSuppressed(definition.Category, SuppressedByPreferences);
            _logger.LogInformation(
                "notification suppressed {NotificationType} to user {RecipientUserId} for business {BusinessId} reason {SuppressReason}",
                request.Type,
                request.RecipientUserId,
                request.BusinessId,
                SuppressedByPreferences);

            return new NotificationResult(false, null, Array.Empty<string>(), SuppressedByPreferences);
        }

        // 4. in_app is delivered inline: the inbox INSERT *is* the delivery, and its
        //    ledger row is written in the same unit of work so in-app is no longer
        //    the only untracked channel (audit defect B4). Operational suppression
        //    is consulted here because in-app rows never reach the dispatch worker.
        Guid? inboxId = null;
        string? inboxSuppression = null;
        if (surviving.Contains(NotificationChannel.InApp) && _dispatchGuard is not null && request.BusinessId.HasValue)
            inboxSuppression = await _dispatchGuard.GetChannelSuppressionReasonAsync(
                request.RecipientUserId, request.BusinessId.Value, NotificationChannel.InApp, ct);

        if (surviving.Contains(NotificationChannel.InApp) && inboxSuppression is null)
            inboxId = await WriteInboxRowAsync(request, persistInline, ct);

        if (surviving.Contains(NotificationChannel.InApp) && inboxSuppression is not null)
        {
            NotificationMetrics.RecordSuppressed(definition.Category, $"suppression:{inboxSuppression}");
            _logger.LogInformation(
                "notification in-app write suppressed {NotificationType} to user {RecipientUserId} for business {BusinessId} reason {SuppressReason}",
                request.Type,
                request.RecipientUserId,
                request.BusinessId,
                inboxSuppression);
        }

        var accepted = new List<string>();
        if (inboxId.HasValue)
        {
            accepted.Add(NotificationChannel.InApp);
            NotificationMetrics.RecordSent(NotificationChannel.InApp);
        }

        foreach (var channel in surviving.Where(channel => channel != NotificationChannel.InApp))
        {
            if (await TryQueueOutboxRowAsync(request, channel, persistInline, ct))
                accepted.Add(channel);
        }

        return new NotificationResult(true, inboxId, accepted);
    }

    /// <summary>R2 — the Force bypass is narrow, and always audited.</summary>
    private IReadOnlyList<string> ForcePastPreferences(NotificationRequest request, NotificationTypeDef definition)
    {
        _logger.LogWarning(
            "notification forced past preferences {NotificationType} to user {RecipientUserId} for business {BusinessId}",
            request.Type,
            request.RecipientUserId,
            request.BusinessId);

        return definition.DefaultChannels;
    }

    /// <summary>Writes the inbox row plus its same-UoW ledger row and returns the inbox id.</summary>
    private async Task<Guid?> WriteInboxRowAsync(NotificationRequest request, bool persistInline, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var now = DateTime.UtcNow;
        var idempotencyKey = CreateIdempotencyKey(request, NotificationChannel.InApp, now);
        var alreadyWritten = await _context.NotificationLogs
            .AsNoTracking()
            .AnyAsync(log => log.IdempotencyKey == idempotencyKey && log.Channel == NotificationChannel.InApp, ct);
        if (alreadyWritten)
            return null;

        var inboxId = Guid.NewGuid();
        var inAppHistory = NotificationDeliveryAttemptHistory.ForNewRecord();
        var inAppAttempt = inAppHistory.Start(now);
        inAppAttempt.CompletedAtUtc = now;
        inAppAttempt.Outcome = "sent";

        await _unitOfWork.Notifications.AddAsync(new Notification
        {
            Id = inboxId,
            UserId = request.RecipientUserId,
            BusinessId = request.BusinessId,
            Type = request.Type,
            // Convention: producers may pass these two well-known display keys and
            // the existing inbox columns/deep-links keep working unchanged.
            AppointmentId = ExtractGuid(request.Data, "appointmentId"),
            StampsCount = ExtractInt(request.Data, "stamps"),
            PayloadJson = SerializeData(request.Data),
            IsRead = false,
            CreatedAt = now
        });

        _context.NotificationLogs.Add(new NotificationLog
        {
            Id = Guid.NewGuid(),
            UserId = request.RecipientUserId,
            BusinessId = request.BusinessId,
            Channel = NotificationChannel.InApp,
            TemplateType = request.Type,
            Status = "sent",
            IdempotencyKey = idempotencyKey,
            SentAt = now,
            DeliveryAttemptsJson = inAppHistory.Serialize(),
            CreatedAt = now
        });

        if (persistInline)
            await _unitOfWork.SaveChangesAsync();
        return inboxId;
    }

    private async Task<bool> TryQueueOutboxRowAsync(
        NotificationRequest request,
        string channel,
        bool persistInline,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var now = DateTime.UtcNow;
        var payload = SerializeData(request.Data);
        var key = CreateIdempotencyKey(request, channel, now);
        if (await _context.NotificationLogs.AnyAsync(
                existing => existing.IdempotencyKey == key, ct))
        {
            return false;
        }

        var row = new NotificationLog
        {
            Id = Guid.NewGuid(),
            UserId = request.RecipientUserId,
            BusinessId = request.BusinessId,
            Channel = channel,
            TemplateType = request.Type,
            Status = "pending",
            PayloadJson = payload,
            Attempts = 0,
            DeliveryAttemptsJson = NotificationDeliveryAttemptHistory.ForNewRecord().Serialize(),
            NextAttemptAt = now,
            IdempotencyKey = key,
            RuleId = request.RuleId,
            UpdatedAt = now,
            CreatedAt = now
        };

        _context.NotificationLogs.Add(row);
        if (!persistInline)
        {
            // Intent-level outbox: the caller's SaveChanges persists this row in the
            // same transaction as its domain change; the idempotency unique index
            // arbitrates any race at commit time.
            return true;
        }

        try
        {
            // A separate SaveChanges per channel keeps an idempotency collision from
            // rolling back an inline in-app delivery already committed above.
            await _context.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (IsIdempotencyUniqueViolation(ex))
        {
            _context.Entry(row).State = EntityState.Detached;
            _logger.LogInformation(
                "notification outbox row already accepted {NotificationType} for channel {Channel} to user {RecipientUserId} for business {BusinessId}",
                request.Type, channel, request.RecipientUserId, request.BusinessId);
            return false;
        }
    }

    private static bool IsIdempotencyUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "ux_notifications_idempotency" };

    private static string CreateIdempotencyKey(
        NotificationRequest request,
        string channel,
        DateTime utcNow)
    {
        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
            return channel == NotificationChannel.Email
                ? request.IdempotencyKey.Trim()
                : $"{request.IdempotencyKey.Trim()}:{channel}";

        var natural = string.Join('|',
            request.Type,
            request.RecipientUserId.ToString("D"),
            request.BusinessId?.ToString("D") ?? "global",
            channel,
            utcNow.ToString("yyyyMMdd"));

        // Keep the natural key deterministic and within varchar(200), even if a
        // future registered type has a long name.
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(natural))).ToLowerInvariant();
        return $"n:{hash}";
    }

    /// <summary>
    /// Fail-closed tenant check: the recipient must own, work at, or be enrolled
    /// with the given business, otherwise nothing is written and the caller gets an
    /// <see cref="ArgumentException"/>.
    /// </summary>
    private async Task EnsureRecipientBelongsToBusinessAsync(
        Guid recipientUserId, Guid businessId, CancellationToken ct)
    {
        var business = await _context.Businesses
            .AsNoTracking()
            .Where(b => b.Id == businessId)
            .Select(b => new { b.OwnerId })
            .FirstOrDefaultAsync(ct);

        if (business == null)
            throw new ArgumentException($"Business {businessId} was not found.", nameof(businessId));

        if (business.OwnerId == recipientUserId) return;

        var isStaff = await _context.Users
            .AsNoTracking()
            .AnyAsync(u => u.Id == recipientUserId && u.StaffBusinessId == businessId, ct);

        if (isStaff) return;

        var isEnrolled = await _context.CustomerBusinessEnrollments
            .AsNoTracking()
            .AnyAsync(e => e.CustomerId == recipientUserId && e.BusinessId == businessId, ct);

        if (isEnrolled) return;

        throw new ArgumentException(
            $"Recipient {recipientUserId} is not associated with business {businessId}.",
            nameof(recipientUserId));
    }

    private string SerializeData(IReadOnlyDictionary<string, object?> data)
    {
        if (data.Count == 0) return "{}";

        try
        {
            return JsonSerializer.Serialize(data);
        }
        catch (Exception ex) when (ex is NotSupportedException or JsonException)
        {
            // The payload is display-only; a non-serializable value must never fail
            // the notification itself, but it must not vanish silently either.
            _logger.LogWarning(ex, "notification payload could not be serialized; storing an empty payload.");
            return "{}";
        }
    }

    private static Guid? ExtractGuid(IReadOnlyDictionary<string, object?> data, string key)
    {
        if (!data.TryGetValue(key, out var value) || value == null) return null;

        return value switch
        {
            Guid guid => guid,
            string text when Guid.TryParse(text, out var parsed) => parsed,
            _ => null
        };
    }

    private static int ExtractInt(IReadOnlyDictionary<string, object?> data, string key)
    {
        if (!data.TryGetValue(key, out var value) || value == null) return 0;

        return value switch
        {
            int number => number,
            long number => (int)number,
            string text when int.TryParse(text, out var parsed) => parsed,
            _ => 0
        };
    }
}
