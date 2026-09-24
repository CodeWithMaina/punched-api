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
/// audit's B4/B7) and — from Phase 2 — queues external channels on the
/// <c>notifications</c> outbox.
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

    public NotificationService(
        IUnitOfWork unitOfWork,
        ApplicationDbContext context,
        IPreferenceResolver preferences,
        ILogger<NotificationService> logger)
    {
        _unitOfWork = unitOfWork;
        _context = context;
        _preferences = preferences;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<NotificationResult> SendAsync(NotificationRequest request, CancellationToken ct = default)
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
        //    the only untracked channel (audit defect B4).
        Guid? inboxId = null;
        if (surviving.Contains(NotificationChannel.InApp))
            inboxId = await WriteInboxRowAsync(request, ct);

        var accepted = new List<string>();
        if (inboxId.HasValue) accepted.Add(NotificationChannel.InApp);

        foreach (var channel in surviving.Where(channel => channel != NotificationChannel.InApp))
        {
            if (await TryQueueOutboxRowAsync(request, channel, ct))
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
    private async Task<Guid> WriteInboxRowAsync(NotificationRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var now = DateTime.UtcNow;
        var inboxId = Guid.NewGuid();

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
            SentAt = now,
            CreatedAt = now
        });

        await _unitOfWork.SaveChangesAsync();
        return inboxId;
    }

    private async Task<bool> TryQueueOutboxRowAsync(
        NotificationRequest request,
        string channel,
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
            NextAttemptAt = now,
            IdempotencyKey = key,
            UpdatedAt = now,
            CreatedAt = now
        };

        _context.NotificationLogs.Add(row);
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
            return request.IdempotencyKey.Trim();

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
