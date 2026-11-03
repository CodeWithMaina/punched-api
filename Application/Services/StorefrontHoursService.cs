using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

/// <summary>
/// Public opening hours — see <see cref="IStorefrontHoursService"/>.
///
/// <para><b>One schedule, presented.</b> The weekly hours are the union of the
/// business's staff working patterns (<see cref="StaffAvailability"/>), which is
/// the same recurring week the availability engine resolves before it offers a
/// slot. When the business has recorded no staff schedules at all, the hours
/// fall back to the business's own booking window
/// (<see cref="Business.BookingOpenHour"/>–<see cref="Business.BookingCloseHour"/>),
/// which is exactly the engine's own fallback — so the page can never advertise
/// hours the booking engine would refuse.</para>
///
/// <para><b>Dated exceptions</b> come from the dated <see cref="StaffShift"/>
/// rows: a date on which the whole team has recorded a day off is reported as a
/// closure; a date with one-off working hours is reported as a special opening.
/// A single member's day off never closes the business, because the others are
/// still working.</para>
///
/// <para>Times are business-local wall-clock, matching how the minutes are
/// stored. No time-zone arithmetic happens here, so a "09:00" shift is reported
/// as "09:00" whatever the server clock says.</para>
/// </summary>
public sealed class StorefrontHoursService : IStorefrontHoursService
{
    /// <summary>How far ahead dated exceptions are published.</summary>
    private const int SpecialHoursHorizonDays = 90;

    /// <summary>A bound on published exceptions, so a long roster of future dates cannot bloat the response.</summary>
    private const int MaxSpecialDays = 60;

    private static readonly string[] DayNames =
        ["sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday"];

    private readonly ApplicationDbContext _db;
    private readonly ILogger<StorefrontHoursService> _logger;
    private readonly ITenantContext? _tenant;

    public StorefrontHoursService(
        ApplicationDbContext db,
        ILogger<StorefrontHoursService> logger,
        ITenantContext? tenant = null)
    {
        _db = db;
        _logger = logger;
        _tenant = tenant;
    }

    private Guid? TenantBusinessId => _tenant?.IsActive == true ? _tenant.BusinessId : null;

    public async Task<ApiResponse<PublicOpeningHoursResponse>> GetPublicOpeningHoursAsync(
        Guid businessId, CancellationToken cancellationToken = default)
    {
        if (businessId == Guid.Empty)
            return ApiResponse<PublicOpeningHoursResponse>.Fail("NOT_FOUND", "Business not found.");

        if (TenantBusinessId is Guid activeTenantId && businessId != activeTenantId)
            return ApiResponse<PublicOpeningHoursResponse>.Fail("NOT_FOUND", "Business not found.");

        try
        {
            var business = await _db.Businesses.AsNoTracking()
                .FirstOrDefaultAsync(b => b.Id == businessId && !b.IsDeleted, cancellationToken);
            if (business == null)
                return ApiResponse<PublicOpeningHoursResponse>.Fail("NOT_FOUND", "Business not found.");

            var activeStaffIds = await _db.BusinessMemberships.AsNoTracking()
                .Where(membership => membership.BusinessId == businessId
                                     && membership.Role == BusinessMembershipRole.Staff
                                     && membership.Status == BusinessMembershipStatus.Active)
                .Select(membership => membership.UserId)
                .ToListAsync(cancellationToken);

            var patternRows = activeStaffIds.Count == 0
                ? []
                : (await _db.StaffAvailability.AsNoTracking()
                    .Where(row => row.BusinessId == businessId && activeStaffIds.Contains(row.StaffUserId))
                    .ToListAsync(cancellationToken));

            var hasStaffSchedule = patternRows.Any(row => row.IsWorking);
            var days = BuildWeeklyHours(business, patternRows, hasStaffSchedule);
            var specialHours = await BuildSpecialHoursAsync(businessId, activeStaffIds, cancellationToken);

            return ApiResponse<PublicOpeningHoursResponse>.Ok(new PublicOpeningHoursResponse
            {
                BusinessId = businessId,
                TimeZoneId = business.TimeZoneId,
                HasStaffSchedule = hasStaffSchedule,
                Days = days,
                SpecialHours = specialHours
            });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error loading public opening hours for business {BusinessId}", businessId);
            return ApiResponse<PublicOpeningHoursResponse>.Fail("LOAD_FAILED", "Failed to load opening hours.");
        }
    }

    /// <summary>Formats a minutes-from-local-midnight value as `HH:mm`.</summary>
    private static string Clock(int minutes)
    {
        var clamped = Math.Clamp(minutes, 0, 24 * 60);
        return $"{clamped / 60:00}:{clamped % 60:00}";
    }

    /// <summary>
    /// The weekly hours. With a roster, a weekday is the union of the shifts
    /// scheduled for it; without one, the business's own booking window applies
    /// to every day (the engine's own fallback).
    /// </summary>
    private static List<OpeningHoursDayResponse> BuildWeeklyHours(
        Business business, List<StaffAvailability> patternRows, bool hasStaffSchedule)
    {
        var days = new List<OpeningHoursDayResponse>(7);
        for (var dayOfWeek = 0; dayOfWeek < 7; dayOfWeek++)
        {
            List<Interval> intervals;
            if (hasStaffSchedule)
            {
                intervals = patternRows
                    .Where(row => row.IsWorking && row.DayOfWeek == dayOfWeek)
                    .Select(row => new Interval(
                        row.StartMinute, row.EndMinute, row.BreakStartMinute, row.BreakEndMinute))
                    .Where(interval => interval.End > interval.Start)
                    .ToList();
            }
            else
            {
                var start = business.BookingOpenHour * 60;
                var end = Math.Max(business.BookingCloseHour * 60, start + 60);
                intervals = [new Interval(start, end, null, null)];
            }

            var periods = MergeIntervals(intervals);
            days.Add(new OpeningHoursDayResponse
            {
                Day = DayNames[dayOfWeek],
                DayOfWeek = dayOfWeek,
                IsClosed = periods.Count == 0,
                Periods = periods
            });
        }

        return days;
    }

    /// <summary>
    /// Merges overlapping/adjacent intervals into display periods. A break is
    /// reported only when every shift contributing to a period declares the same
    /// one — otherwise the business is genuinely covered through the interval.
    /// </summary>
    private static List<OpeningHoursPeriodResponse> MergeIntervals(List<Interval> intervals)
    {
        var ordered = intervals.OrderBy(interval => interval.Start).ThenBy(interval => interval.End).ToList();
        var periods = new List<OpeningHoursPeriodResponse>();

        var index = 0;
        while (index < ordered.Count)
        {
            var opensAt = ordered[index].Start;
            var closesAt = ordered[index].End;
            var group = new List<Interval> { ordered[index] };
            var next = index + 1;

            while (next < ordered.Count && ordered[next].Start <= closesAt)
            {
                group.Add(ordered[next]);
                closesAt = Math.Max(closesAt, ordered[next].End);
                next++;
            }

            int? breakStart = null;
            int? breakEnd = null;
            if (group.All(item => item.BreakStart.HasValue && item.BreakEnd.HasValue))
            {
                var starts = group.Select(item => item.BreakStart!.Value).Distinct().ToList();
                var ends = group.Select(item => item.BreakEnd!.Value).Distinct().ToList();
                if (starts.Count == 1 && ends.Count == 1
                    && starts[0] >= opensAt && ends[0] > starts[0] && ends[0] <= closesAt)
                {
                    breakStart = starts[0];
                    breakEnd = ends[0];
                }
            }

            periods.Add(new OpeningHoursPeriodResponse
            {
                OpensAt = Clock(opensAt),
                ClosesAt = Clock(closesAt),
                BreakStartsAt = breakStart is null ? null : Clock(breakStart.Value),
                BreakEndsAt = breakEnd is null ? null : Clock(breakEnd.Value)
            });

            index = next;
        }

        return periods;
    }

    /// <summary>
    /// Dated exceptions within the horizon: a date the whole team has taken off
    /// (a closure) or a date with one-off working hours.
    /// </summary>
    private async Task<List<SpecialOpeningHoursResponse>> BuildSpecialHoursAsync(
        Guid businessId, List<Guid> activeStaffIds, CancellationToken cancellationToken)
    {
        if (activeStaffIds.Count == 0) return [];

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var horizon = today.AddDays(SpecialHoursHorizonDays);

        var datedRows = await _db.StaffShifts.AsNoTracking()
            .Where(shift => shift.BusinessId == businessId
                            && shift.Date >= today && shift.Date <= horizon
                            && activeStaffIds.Contains(shift.StaffUserId))
            .ToListAsync(cancellationToken);
        if (datedRows.Count == 0) return [];

        var specialHours = new List<SpecialOpeningHoursResponse>();
        foreach (var group in datedRows.GroupBy(row => row.Date).OrderBy(group => group.Key))
        {
            if (specialHours.Count >= MaxSpecialDays) break;

            var rows = group.ToList();
            var working = rows.Where(row => row.IsWorking).ToList();
            var label = rows.Select(row => row.Note).FirstOrDefault(note => !string.IsNullOrWhiteSpace(note));

            if (working.Count == 0)
            {
                // A closure is only reported when EVERY active member recorded the
                // day off — one person's day off does not close the business.
                var recordedStaff = rows.Select(row => row.StaffUserId).Distinct().Count();
                if (recordedStaff < activeStaffIds.Count) continue;

                specialHours.Add(new SpecialOpeningHoursResponse
                {
                    Date = group.Key.ToString("yyyy-MM-dd"),
                    IsClosed = true,
                    Label = label
                });
                continue;
            }

            var intervals = working
                .Select(row => new Interval(
                    row.StartMinute ?? row.StartHour * 60,
                    row.EndMinute ?? row.EndHour * 60,
                    row.BreakStartMinute,
                    row.BreakEndMinute))
                .Where(interval => interval.End > interval.Start)
                .ToList();

            var periods = MergeIntervals(intervals);
            if (periods.Count == 0) continue;

            specialHours.Add(new SpecialOpeningHoursResponse
            {
                Date = group.Key.ToString("yyyy-MM-dd"),
                IsClosed = false,
                Label = label,
                Periods = periods
            });
        }

        return specialHours;
    }

    /// <summary>A half-open local interval, in minutes from midnight.</summary>
    private readonly record struct Interval(int Start, int End, int? BreakStart, int? BreakEnd);
}
