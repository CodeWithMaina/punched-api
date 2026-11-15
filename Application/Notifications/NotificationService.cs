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

    /// <summary>
    /// Provider-configuration availability; null only in unit tests (the Force path
    /// then keeps its historical "all declared channels" behaviour).
    /// </summary>
    private readonly INotificationChannelAvailability? _channelAvailability;

    /// <summary>
    /// Answers that cannot change within one request, remembered so a booking that
    /// queues several notifications for the same person does not re-ask the same
    /// question — and pay another database round trip — for each of them.
    /// </summary>
    private readonly HashSet<(Guid RecipientUserId, Guid BusinessId)> _verifiedRecipients = new();
    private readonly Dictionary<(Guid RecipientUserId, Guid BusinessId, string Channel), string?> _suppressionReasons = new();

    public NotificationService(
        IUnitOfWork unitOfWork,
        ApplicationDbContext context,
        IPreferenceResolver preferences,
        ILogger<NotificationService> logger,
        INotificationDispatchGuard? dispatchGuard = null,
        INotificationChannelAvailability? channelAvailability = null)
    {
        _unitOfWork = unitOfWork;
        _context = context;
        _preferences = preferences;
        _logger = logger;
        _dispatchGuard = dispatchGuard;
        _channelAvailability = channelAvailability;
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
        //    client input; a mismatched recipient writes nothing. Memoised per
        //    (recipient, business): one appointment queues several notifications for
        //    the same pair, and the guard's answer cannot change mid-request.
        if (request.BusinessId.HasValue)
        {
            var guardKey = (request.RecipientUserId, request.BusinessId.Value);
            if (!_verifiedRecipients.Contains(guardKey))
            {
                await EnsureRecipientBelongsToBusinessAsync(request.RecipientUserId, request.BusinessId.Value, ct);
                _verifiedRecipients.Add(guardKey);
            }
        }

        // 3. Preferences. R2 — only SECURITY may bypass them.
        var surviving = request.Force && definition.Category == NotificationCategory.Security
            ? await ForcePastPreferencesAsync(request, definition, ct)
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

        // 4. Idempotency: every key this call could write, resolved in ONE query.
        //    The date bucket in the key is taken once, so the keys computed here are
        //    exactly the keys the row writers would otherwise compute themselves.
        var now = DateTime.UtcNow;
        var wantsInApp = surviving.Contains(NotificationChannel.InApp);
        var externalChannels = surviving.Where(channel => channel != NotificationChannel.InApp).ToList();

        var inAppKey = wantsInApp ? CreateIdempotencyKey(request, NotificationChannel.InApp, now) : null;
        var channelKeys = externalChannels
            .Select(channel => (Channel: channel, Key: CreateIdempotencyKey(request, channel, now)))
            .ToList();

        var candidateKeys = new List<string>();
        if (inAppKey != null) candidateKeys.Add(inAppKey);
        candidateKeys.AddRange(channelKeys.Select(entry => entry.Key));

        var alreadyQueued = candidateKeys.Count == 0
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(
                await _context.NotificationLogs
                    .AsNoTracking()
                    .Where(log => candidateKeys.Contains(log.IdempotencyKey!))
                    .Select(log => log.IdempotencyKey!)
                    .ToListAsync(ct),
                StringComparer.Ordinal);

        // 5. in_app is delivered inline: the inbox INSERT *is* the delivery, and its
        //    ledger row is written in the same unit of work so in-app is no longer
        //    the only untracked channel (audit defect B4). Operational suppression
        //    is consulted here because in-app rows never reach the dispatch worker.
        Guid? inboxId = null;
        string? inboxSuppression = null;
        if (wantsInApp && _dispatchGuard is not null && request.BusinessId.HasValue)
            inboxSuppression = await SuppressionReasonAsync(
                request.RecipientUserId, request.BusinessId.Value, NotificationChannel.InApp, ct);

        if (wantsInApp && inboxSuppression is null)
            inboxId = await WriteInboxRowAsync(
                request, inAppKey!, alreadyQueued.Contains(inAppKey!), now, persistInline, ct);

        if (wantsInApp && inboxSuppression is not null)
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

        foreach (var (channel, key) in channelKeys)
        {
            if (alreadyQueued.Contains(key)) continue;
            if (await TryQueueOutboxRowAsync(request, channel, key, now, persistInline, ct))
                accepted.Add(channel);
        }

        return new NotificationResult(true, inboxId, accepted);
    }

    /// <summary>R2 — the Force bypass is narrow, and always audited.</summary>
    /// <remarks>
    /// Force bypasses <em>preferences</em>, never <em>availability</em>: a channel
    /// this deployment cannot deliver on (SMTP off, no VAPID keys, no mobile
    /// number) must not be queued just to dead-letter. Unit-test providers that
    /// register no availability service keep the historical "all declared
    /// channels" behaviour.
    /// </remarks>
    private async Task<IReadOnlyList<string>> ForcePastPreferencesAsync(
        NotificationRequest request, NotificationTypeDef definition, CancellationToken ct)
    {
        _logger.LogWarning(
            "notification forced past preferences {NotificationType} to user {RecipientUserId} for business {BusinessId}",
            request.Type,
            request.RecipientUserId,
            request.BusinessId);

        if (_channelAvailability is null)
            return definition.DefaultChannels;

        return await _channelAvailability.FilterAvailableAsync(
            request.RecipientUserId, definition.DefaultChannels, ct);
    }

    /// <summary>Writes the inbox row plus its same-UoW ledger row and returns the inbox id.</summary>
    /// <remarks>
    /// The idempotency key and whether it already exists are resolved by the caller in
    /// the single batched pre-check, so this method performs no query of its own.
    /// </remarks>
    private async Task<Guid?> WriteInboxRowAsync(
        NotificationRequest request, string idempotencyKey, bool alreadyWritten, DateTime now,
        bool persistInline, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
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
        string key,
        DateTime now,
        bool persistInline,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var payload = SerializeData(request.Data);

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
    /// <remarks>
    /// The happy path is ONE round trip: membership and enrollment are asked as a
    /// single <c>UNION</c>, and the "does this business exist at all" question is only
    /// asked when the answer is already a refusal — which is the only case that needs
    /// to tell the two failures apart.
    /// </remarks>
    private async Task EnsureRecipientBelongsToBusinessAsync(
        Guid recipientUserId, Guid businessId, CancellationToken ct)
    {
        var associated = await _context.BusinessMemberships
            .AsNoTracking()
            .Where(m => m.UserId == recipientUserId &&
                        m.BusinessId == businessId &&
                        m.Status == BusinessMembershipStatus.Active)
            .Select(_ => 1)
            .Union(_context.CustomerBusinessEnrollments
                .AsNoTracking()
                .Where(e => e.CustomerId == recipientUserId && e.BusinessId == businessId)
                .Select(_ => 1))
            .AnyAsync(ct);

        if (associated) return;

        var businessExists = await _context.Businesses
            .AsNoTracking()
            .AnyAsync(b => b.Id == businessId, ct);

        throw new ArgumentException(
            businessExists
                ? $"Recipient {recipientUserId} is not associated with business {businessId}."
                : $"Business {businessId} was not found.",
            businessExists ? nameof(recipientUserId) : nameof(businessId));
    }

    /// <summary>
    /// The dispatch guard's suppression verdict, memoised per (recipient, business,
    /// channel) for the life of the scope: a booking asks the same question for the
    /// same person when it queues each of its notifications.
    /// </summary>
    private async Task<string?> SuppressionReasonAsync(
        Guid recipientUserId, Guid businessId, string channel, CancellationToken ct)
    {
        var key = (recipientUserId, businessId, channel);
        if (_suppressionReasons.TryGetValue(key, out var cached)) return cached;

        var reason = await _dispatchGuard!.GetChannelSuppressionReasonAsync(
            recipientUserId, businessId, channel, ct);
        _suppressionReasons[key] = reason;
        return reason;
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
