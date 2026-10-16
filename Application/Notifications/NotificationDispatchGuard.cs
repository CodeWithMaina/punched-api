using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Notifications;

/// <summary>
/// Dispatch-time guard consulted before any external channel send (Phase 1.6).
/// Every check reads database state, so enforcement is correct across API
/// instances: operational suppression rows, per-(business, user, channel)
/// rolling-window frequency caps, business-timezone quiet hours, and per-tenant
/// SMS/push daily budgets.
/// </summary>
/// <remarks>
/// SECURITY types bypass frequency caps and quiet hours (G6 — an OTP must land)
/// but never bypass operational suppression or cost budgets. In-app has no
/// dispatch step, so its suppression check happens at acceptance via
/// <see cref="GetChannelSuppressionReasonAsync"/>.
/// </remarks>
public interface INotificationDispatchGuard
{
    /// <summary>Full verdict for one external outbox row before channel dispatch.</summary>
    Task<DispatchVerdict> EvaluateAsync(NotificationLog row, CancellationToken ct = default);

    /// <summary>Active suppression reason for a channel, or null when not suppressed.</summary>
    Task<string?> GetChannelSuppressionReasonAsync(
        Guid userId, Guid businessId, string channel, CancellationToken ct = default);
}

/// <summary>
/// Outcome of a dispatch guard evaluation. Denials carry the earliest UTC time
/// the row becomes eligible again so the worker can defer instead of consuming
/// a delivery attempt — deferral, never destruction, is the guard's contract.
/// </summary>
public sealed record DispatchVerdict(bool Allow, DateTime? RetryAtUtc = null, string? Reason = null)
{
    public static readonly DispatchVerdict Proceed = new(true);

    public static DispatchVerdict Defer(DateTime retryAtUtc, string reason) => new(false, retryAtUtc, reason);
}

public sealed class NotificationDispatchGuard : INotificationDispatchGuard
{
    private readonly ApplicationDbContext _context;
    private readonly NotificationWorkerOptions _options;

    public NotificationDispatchGuard(
        ApplicationDbContext context,
        IOptions<NotificationWorkerOptions>? options = null)
    {
        _context = context;
        _options = options?.Value ?? new NotificationWorkerOptions();
    }

    /// <inheritdoc />
    public async Task<DispatchVerdict> EvaluateAsync(NotificationLog row, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        // 1. Operational suppression — hard, channel-level, always wins (target
        //    architecture §4: bounces/unsubscribes with reason and expiry).
        if (row.BusinessId.HasValue)
        {
            var suppressionReason = await GetChannelSuppressionReasonAsync(
                row.UserId, row.BusinessId.Value, row.Channel, ct);
            if (suppressionReason != null)
            {
                var expiry = await _context.NotificationSuppressions
                    .AsNoTracking()
                    .Where(s => s.BusinessId == row.BusinessId.Value
                        && s.UserId == row.UserId
                        && s.Channel == row.Channel
                        && (s.ExpiresAt == null || s.ExpiresAt > now))
                    .Select(s => s.ExpiresAt)
                    .FirstOrDefaultAsync(ct);
                var retryAt = expiry ?? now.AddHours(1);
                if (retryAt <= now) retryAt = now.AddMinutes(10);
                return DispatchVerdict.Defer(retryAt, $"suppression:{suppressionReason}");
            }
        }

        // Unknown mid-flight type: nothing to defer — the worker dead-letters
        // unregistered types before consulting the guard.
        var isSecurity = NotificationTypes.TryGet(row.TemplateType, out var definition)
            && definition.IsSecurity;

        // 2. Frequency cap — rolling window per (business, user, channel), non-SECURITY
        //    only. The row itself already counts (it was admitted at queue time), so the
        //    cap triggers only when admissions in the window exceed it.
        if (row.BusinessId.HasValue && !isSecurity && _options.FrequencyCapPerUserChannel > 0)
        {
            var windowStart = now.AddMinutes(-Math.Max(1, _options.FrequencyWindowMinutes));
            var query = _context.NotificationLogs
                .AsNoTracking()
                .Where(l => l.BusinessId == row.BusinessId.Value
                    && l.UserId == row.UserId
                    && l.Channel == row.Channel
                    && l.CreatedAt >= windowStart);

            var admissions = await query.CountAsync(ct);
            if (admissions > _options.FrequencyCapPerUserChannel)
            {
                var oldest = await query
                    .OrderBy(l => l.CreatedAt)
                    .Select(l => (DateTime?)l.CreatedAt)
                    .FirstOrDefaultAsync(ct);
                var retryAt = (oldest ?? windowStart)
                    .AddMinutes(Math.Max(1, _options.FrequencyWindowMinutes))
                    .AddSeconds(1);
                if (retryAt < now.AddSeconds(30)) retryAt = now.AddSeconds(30);
                return DispatchVerdict.Defer(retryAt, "frequency_cap");
            }
        }

        // 3. Quiet hours — business timezone, non-SECURITY only. Storage stays UTC;
        //    the business's IANA timezone governs the local quiet window.
        if (row.BusinessId.HasValue && !isSecurity)
        {
            var quietExit = NextQuietHourExitUtc(now, row.BusinessId.Value, ct);
            if (quietExit != null)
                return DispatchVerdict.Defer(quietExit.Value, "quiet_hours");
        }

        // 4. Per-tenant SMS/push budget — admissions today (UTC day) across every
        //    instance; SECURITY types are included because the cost gate still applies.
        if (row.BusinessId.HasValue
            && (row.Channel == NotificationChannel.Sms || row.Channel == NotificationChannel.Push))
        {
            var cap = row.Channel == NotificationChannel.Sms
                ? _options.SmsBudgetPerBusinessPerDay
                : _options.PushBudgetPerBusinessPerDay;
            if (cap > 0)
            {
                var dayStart = now.Date;
                var admittedToday = await _context.NotificationLogs
                    .AsNoTracking()
                    .CountAsync(l => l.BusinessId == row.BusinessId.Value
                        && l.Channel == row.Channel
                        && l.CreatedAt >= dayStart, ct);
                if (admittedToday > cap)
                    return DispatchVerdict.Defer(dayStart.AddDays(1).AddMinutes(1), "budget_exceeded");
            }
        }

        return DispatchVerdict.Proceed;
    }

    /// <inheritdoc />
    public async Task<string?> GetChannelSuppressionReasonAsync(
        Guid userId, Guid businessId, string channel, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var reason = await _context.NotificationSuppressions
            .AsNoTracking()
            .Where(s => s.BusinessId == businessId
                && s.UserId == userId
                && s.Channel == channel
                && (s.ExpiresAt == null || s.ExpiresAt > now))
            .Select(s => (string?)s.Reason)
            .FirstOrDefaultAsync(ct);
        return reason;
    }

    /// <summary>
    /// Next UTC instant at which local quiet hours end for the business, or null
    /// when now is outside quiet hours (or the timezone cannot be resolved — quiet
    /// hours then simply do not apply).
    /// </summary>
    private DateTime? NextQuietHourExitUtc(DateTime nowUtc, Guid businessId, CancellationToken ct)
    {
        var startHour = _options.QuietHoursStartHour;
        var endHour = _options.QuietHoursEndHour;
        if (startHour == endHour || startHour is < 0 or > 23 || endHour is < 0 or > 23)
            return null;

        var timeZoneId = _context.Businesses
            .AsNoTracking()
            .Where(b => b.Id == businessId)
            .Select(b => b.TimeZoneId)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(timeZoneId)) return null;

        TimeZoneInfo timeZone;
        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return null;
        }
        catch (InvalidTimeZoneException)
        {
            return null;
        }

        var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, timeZone);
        var inQuiet = startHour > endHour
            ? local.Hour >= startHour || local.Hour < endHour
            : local.Hour >= startHour && local.Hour < endHour;
        if (!inQuiet) return null;

        var nextLocal = local.Date.AddHours(endHour);
        if (nextLocal <= local) nextLocal = nextLocal.AddDays(1);
        try
        {
            return TimeZoneInfo.ConvertTimeToUtc(nextLocal, timeZone);
        }
        catch (ArgumentException)
        {
            return nowUtc.AddHours(1);
        }
    }
}
