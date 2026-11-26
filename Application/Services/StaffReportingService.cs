using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

/// <summary>
/// Owner-side staff reporting (financial contribution and performance).
///
/// <para><b>One window, many aggregates.</b> Each report resolves the period once,
/// then loads every number with a small, fixed number of GROUP BY queries — one per
/// fact table, scoped to the business and the window. Rows are stitched together in
/// memory over at most one entry per staff member, so the cost is driven by head
/// count, never by the number of appointments. There is no per-staff round trip.</para>
///
/// <para><b>Attribution.</b> Work is attributed to the member recorded on the
/// appointment (<c>appointments.staff_user_id</c>). A payment is attributed through
/// the appointment it settles, which keeps money and work on the same person even
/// when the payment was taken at the counter by someone else.</para>
///
/// <para><b>Historical prices.</b> Revenue reads
/// <see cref="AppointmentResource.Price"/> — the price snapshotted at booking — so
/// editing a service today never restates a completed month.</para>
/// </summary>
public partial class StaffReportingService : IStaffReportingService
{
    private const int MaxWindowDays = 366;
    private const string Currency = "KES";

    private static readonly PaymentStatus[] SettledStatuses =
        [PaymentStatus.Success, PaymentStatus.Verified];

    private static readonly PaymentStatus[] RefundedStatuses =
        [PaymentStatus.Refunded, PaymentStatus.Reversed];

    private static readonly PaymentStatus[] AwaitingDecisionStatuses =
        [PaymentStatus.Submitted, PaymentStatus.UnderReview, PaymentStatus.Flagged];

    private readonly ApplicationDbContext _context;
    private readonly IBusinessScopeResolver _scopeResolver;
    private readonly ILogger<StaffReportingService> _logger;

    /// <summary>Active tenant (null on the platform root and in unit tests) — scoping only, never a grant.</summary>
    private readonly ITenantContext? _tenant;

    public StaffReportingService(
        ApplicationDbContext context,
        IBusinessScopeResolver scopeResolver,
        ILogger<StaffReportingService> logger,
        ITenantContext? tenant = null)
    {
        _context = context;
        _scopeResolver = scopeResolver;
        _logger = logger;
        _tenant = tenant;
    }

    /// <summary>The resolved business, or null when the caller owns none.</summary>
    private async Task<Guid?> ResolveBusinessIdAsync(Guid callerUserId, CancellationToken cancellationToken)
    {
        if (_tenant?.IsActive == true && _tenant.BusinessId != Guid.Empty)
            return _tenant.BusinessId;

        if (callerUserId == Guid.Empty) return null;

        var owned = await _scopeResolver.GetOwnedBusinessIdAsync(callerUserId);
        return owned ?? await _scopeResolver.GetMemberBusinessIdAsync(callerUserId);
    }
}

/// <summary>
/// Shared window and roster helpers for both reports.
/// </summary>
public partial class StaffReportingService
{
    /// <summary>A resolved reporting window plus the equal-length window before it.</summary>
    private sealed record Window(
        DateOnly LocalFrom,
        DateOnly LocalTo,
        DateOnly LocalPreviousFrom,
        DateOnly LocalPreviousTo,
        DateTime FromUtc,
        DateTime EndExclusiveUtc,
        DateTime PreviousFromUtc,
        DateTime PreviousEndExclusiveUtc,
        string Label,
        string RangeKey,
        string TimeZoneId);

    /// <summary>
    /// Parses the requested window. <c>from</c>/<c>to</c> (inclusive calendar days in the business's timezone) win over a preset; the window then reports itself as "custom" so the UI
    /// does not claim to be showing "Last 30 days" when it is not.
    /// </summary>
    private static (Window? Window, string? ErrorCode, string? ErrorMessage) ResolveWindow(
        StaffReportQuery query, TimeZoneInfo timeZone)
    {
        // Presets are calendar days in the BUSINESS's timezone. Today in Nairobi is not
        // today in UTC, and a DST day is 23 or 25 hours long — so the local date is
        // chosen first and only then converted to the UTC instants the query uses.
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, timeZone));
        DateOnly from;
        DateOnly to;
        string label;
        string rangeKey;

        if (query.From.HasValue || query.To.HasValue)
        {
            from = query.From ?? query.To ?? today;
            to = query.To ?? query.From ?? today;
            rangeKey = "custom";
            label = "Custom range";
        }
        else
        {
            switch ((query.Range ?? "30d").Trim().ToLowerInvariant())
            {
                case "today":
                    from = today; to = today; rangeKey = "today"; label = "Today";
                    break;
                case "7d":
                    from = today.AddDays(-6); to = today; rangeKey = "7d"; label = "Last 7 days";
                    break;
                case "this_month":
                    from = new DateOnly(today.Year, today.Month, 1); to = today;
                    rangeKey = "this_month"; label = "This month";
                    break;
                case "last_month":
                {
                    var lastMonth = new DateOnly(today.Year, today.Month, 1).AddMonths(-1);
                    from = lastMonth;
                    to = lastMonth.AddMonths(1).AddDays(-1);
                    rangeKey = "last_month";
                    label = "Last month";
                    break;
                }
                case "30d":
                    from = today.AddDays(-29); to = today; rangeKey = "30d"; label = "Last 30 days";
                    break;
                default:
                    return (null, "INVALID_RANGE",
                        "range must be one of today, 7d, 30d, this_month, last_month.");
            }
        }

        if (to < from)
            return (null, "INVALID_RANGE", "The end date cannot be before the start date.");

        var days = to.DayNumber - from.DayNumber + 1;
        if (days > MaxWindowDays)
            return (null, "INVALID_RANGE", $"Choose a range of at most {MaxWindowDays} days.");

        // Local midnight → UTC. Invalid DST times are tolerated by the shared helper,
        // which is the same conversion the booking engine uses for business hours.
        var fromUtc = BookingTime.ToUtc(from.ToDateTime(TimeOnly.MinValue), timeZone);
        var endExclusiveUtc = BookingTime.ToUtc(to.AddDays(1).ToDateTime(TimeOnly.MinValue), timeZone);

        var previousTo = from.AddDays(-1);
        var previousFrom = previousTo.AddDays(-(days - 1));
        var previousFromUtc = BookingTime.ToUtc(previousFrom.ToDateTime(TimeOnly.MinValue), timeZone);
        var previousEndExclusiveUtc = fromUtc;

        return (new Window(
            LocalFrom: from,
            LocalTo: to,
            LocalPreviousFrom: previousFrom,
            LocalPreviousTo: previousTo,
            FromUtc: fromUtc,
            EndExclusiveUtc: endExclusiveUtc,
            PreviousFromUtc: previousFromUtc,
            PreviousEndExclusiveUtc: previousEndExclusiveUtc,
            Label: label,
            RangeKey: rangeKey,
            TimeZoneId: timeZone.Id), null, null);
    }
    private static StaffReportPeriodResponse ToPeriodResponse(Window window) => new()
    {
        From = window.LocalFrom.ToDateTime(TimeOnly.MinValue),
        To = window.LocalTo.ToDateTime(TimeOnly.MinValue),
        FromUtc = window.FromUtc,
        ToUtcExclusive = window.EndExclusiveUtc,
        TimeZoneId = window.TimeZoneId,
        PreviousFrom = window.LocalPreviousFrom.ToDateTime(TimeOnly.MinValue),
        PreviousTo = window.LocalPreviousTo.ToDateTime(TimeOnly.MinValue),
        Label = window.Label,
        Range = window.RangeKey
    };
}

/// <summary>Roster loading and the arithmetic both reports share.</summary>
public partial class StaffReportingService
{
    /// <summary>One member of staff, as the reports need to describe them.</summary>
    private sealed record RosterMember(
        Guid UserId,
        string FullName,
        string Email,
        string? AvatarUrl,
        string MembershipStatus,
        bool IsActive,
        string? PositionName,
        DateTime JoinedAt);

    /// <summary>
    /// The business's team: every staff membership that has not LEFT, including
    /// suspended members. History is not deleted when somebody is deactivated, and a
    /// report that hid them would silently erase their past contribution.
    /// </summary>
    private async Task<List<RosterMember>> LoadRosterAsync(Guid businessId, CancellationToken cancellationToken)
    {
        var memberships = await _context.BusinessMemberships.AsNoTracking()
            .Where(m => m.BusinessId == businessId
                && m.Role == BusinessMembershipRole.Staff
                && m.Status != BusinessMembershipStatus.Left)
            .Select(m => new { m.UserId, m.Status, m.PositionId, m.JoinedAt })
            .ToListAsync(cancellationToken);

        if (memberships.Count == 0)
            return [];

        // One membership per (user, business) is a database invariant; the guard is
        // for a legacy row that predates it.
        memberships = memberships
            .GroupBy(m => m.UserId)
            .Select(g => g.OrderByDescending(m => m.JoinedAt).First())
            .ToList();

        var userIds = memberships.Select(m => m.UserId).ToList();

        var users = await _context.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.FullName, u.Email, u.AvatarUrl, u.CreatedAt })
            .ToListAsync(cancellationToken);

        var positionIds = memberships
            .Where(m => m.PositionId.HasValue)
            .Select(m => m.PositionId!.Value)
            .Distinct()
            .ToList();

        var positions = await _context.StaffPositions.AsNoTracking()
            .Where(p => p.BusinessId == businessId && positionIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Name })
            .ToDictionaryAsync(p => p.Id, p => p.Name, cancellationToken);

        var userById = users.ToDictionary(u => u.Id);

        return memberships
            .Where(m => userById.ContainsKey(m.UserId))
            .Select(m =>
            {
                var user = userById[m.UserId];
                var active = m.Status == BusinessMembershipStatus.Active;

                return new RosterMember(
                    UserId: m.UserId,
                    FullName: user.FullName,
                    Email: user.Email,
                    AvatarUrl: user.AvatarUrl,
                    MembershipStatus: active ? "Active" : "Suspended",
                    IsActive: active,
                    PositionName: m.PositionId.HasValue && positions.TryGetValue(m.PositionId.Value, out var name)
                        ? name
                        : null,
                    JoinedAt: m.JoinedAt == default ? user.CreatedAt : m.JoinedAt);
            })
            .OrderByDescending(m => m.JoinedAt)
            .ThenBy(m => m.UserId)
            .ToList();
    }

    private static decimal Percent(decimal numerator, decimal denominator) =>
        denominator <= 0 ? 0 : Math.Round(numerator / denominator * 100m, 1);

    private static decimal Money(decimal value) => Math.Round(value, 2);

    /// <summary>Change from the previous window, or null when there is no baseline.</summary>
    private static decimal? ChangePercent(decimal current, decimal previous) =>
        previous <= 0 ? null : Math.Round((current - previous) / previous * 100m, 1);

    private static string TrendOf(decimal? changePercent) => changePercent switch
    {
        null => "none",
        > 2m => "up",
        < -2m => "down",
        _ => "flat"
    };

    private static (int Page, int PageSize) Paging(StaffReportQuery query) =>
        (Math.Max(1, query.Page), Math.Clamp(query.PageSize, 1, 100));

    private static string NormalizeStatus(string? status) =>
        (status ?? "all").Trim().ToLowerInvariant() switch
        {
            "active" => "active",
            "inactive" or "suspended" => "inactive",
            _ => "all"
        };

    private static bool MatchesStatus(RosterMember member, string status) => status switch
    {
        "active" => member.IsActive,
        "inactive" => !member.IsActive,
        _ => true
    };

    private static bool MatchesSearch(RosterMember member, string? search)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;

        var needle = search.Trim();
        return member.FullName.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || member.Email.Contains(needle, StringComparison.OrdinalIgnoreCase)
            || (member.PositionName?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false);
    }
}
