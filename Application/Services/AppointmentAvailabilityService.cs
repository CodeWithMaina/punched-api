using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

/// <summary>
/// Outcome of resolving a concrete booking request against the grid: either the
/// staff member who will take the appointment, or the reason it cannot be booked.
/// </summary>
public record SlotResolution(bool Ok, Guid? StaffUserId, string? ErrorCode, string? ErrorMessage)
{
    public static SlotResolution Allow(Guid? staffUserId) => new(true, staffUserId, null, null);
    public static SlotResolution Deny(string code, string message) => new(false, null, code, message);
}

/// <summary>
/// Slot engine for the booking system. Builds the bookable grid from business
/// opening hours, staff shifts, staff-service assignment and existing
/// appointments, in the business's own timezone.
///
/// Unlike a pure "free slots" engine this emits every slot on the grid with a
/// status (available / booked / off_duty / past), so the booking UI can render a
/// full day and mark what is taken instead of showing an empty state. A staff
/// member with no shift row for a date falls back to the business default
/// window: an empty roster never means "closed".
/// </summary>
public class AppointmentAvailabilityService
{
    private const int MaxRangeDays = 31;
    private const int MaxSuggestions = 4;

    private static readonly ConcurrentDictionary<string, TimeZoneInfo> TimeZoneCache = new();

    private readonly ApplicationDbContext _context;
    private readonly ILogger<AppointmentAvailabilityService> _logger;

    public AppointmentAvailabilityService(
        ApplicationDbContext context,
        ILogger<AppointmentAvailabilityService> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>
    /// Legacy flat projection: only the bookable slots. Kept for existing consumers
    /// of GET /v1/businesses/{id}/availability.
    /// </summary>
    public async Task<ApiResponse<List<AvailabilitySlotResponse>>> GetAvailableSlotsAsync(
        Guid businessId,
        Guid[] serviceIds,
        Guid? staffUserId,
        DateOnly startDate,
        DateOnly endDate)
    {
        var calendar = await GetAvailabilityCalendarAsync(businessId, serviceIds, staffUserId, startDate, endDate);
        if (!calendar.Success || calendar.Data == null)
            return ApiResponse<List<AvailabilitySlotResponse>>.Fail(
                calendar.Error?.Code ?? "VALIDATION_ERROR",
                calendar.Error?.Message ?? "Could not compute availability.");

        var flat = calendar.Data.Days
            .SelectMany(d => d.Slots)
            .Where(s => s.IsAvailable)
            .OrderBy(s => s.StartAtUtc)
            .ThenBy(s => s.StaffUserId)
            .ToList();

        return ApiResponse<List<AvailabilitySlotResponse>>.Ok(flat);
    }

    /// <summary>
    /// Full bookable grid for [startDate, endDate] (business-local dates), including
    /// blocked slots, per-day rollups and alternative-staff suggestions.
    /// </summary>
    public async Task<ApiResponse<AvailabilityCalendarResponse>> GetAvailabilityCalendarAsync(
        Guid businessId,
        Guid[] serviceIds,
        Guid? staffUserId,
        DateOnly startDate,
        DateOnly endDate)
    {
        if (serviceIds == null || serviceIds.Length == 0)
            return Fail("VALIDATION_ERROR", "At least one service is required.");

        var business = await _context.Businesses.AsNoTracking().FirstOrDefaultAsync(b => b.Id == businessId);
        if (business == null)
            return Fail("NOT_FOUND", "Business not found.");

        var tz = ResolveTimeZone(business.TimeZoneId);
        var nowUtc = DateTime.UtcNow;

        // Clamp the window: never before today (business-local), never wider than a month.
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz));
        if (startDate == default || startDate < today) startDate = today;
        if (endDate < startDate) endDate = startDate;
        if (endDate.DayNumber - startDate.DayNumber >= MaxRangeDays)
            endDate = startDate.AddDays(MaxRangeDays - 1);

        var distinctRequested = serviceIds.Distinct().ToArray();
        var services = await _context.ServiceCatalogItems.AsNoTracking()
            .Where(s => distinctRequested.Contains(s.Id) && s.BusinessId == businessId && s.IsActive)
            .ToListAsync();
        if (services.Count != distinctRequested.Length)
            return Fail("SERVICE_NOT_FOUND", "One or more services were not found in this business.");

        var totalMinutes = services.Sum(s => s.DurationMinutes);
        if (totalMinutes <= 0)
            return Fail("VALIDATION_ERROR", "The selected services have no duration configured. Ask the business to set a service duration.");

        var interval = NormalizeInterval(business.BookingSlotIntervalMinutes);
        var leadMinutes = Math.Max(0, business.BookingLeadTimeMinutes);

        var response = new AvailabilityCalendarResponse
        {
            BusinessId = business.Id,
            BusinessName = business.Name,
            TimeZoneId = business.TimeZoneId,
            ServiceIds = distinctRequested,
            TotalDurationMinutes = totalMinutes,
            SlotIntervalMinutes = interval,
            LeadTimeMinutes = leadMinutes,
            StaffUserId = staffUserId
        };

        // Eligible roster: staff assigned to every requested service.
        var eligible = await GetEligibleStaffAsync(businessId, distinctRequested);

        if (staffUserId.HasValue)
        {
            var focus = eligible.FirstOrDefault(u => u.Id == staffUserId.Value);
            if (focus == null)
            {
                var existsInBusiness = await _context.Users.AsNoTracking()
                    .AnyAsync(u => u.Id == staffUserId.Value && u.StaffBusinessId == businessId);
                return existsInBusiness
                    ? Fail("STAFF_NOT_AVAILABLE", "This staff member does not perform all of the selected services.")
                    : Fail("STAFF_NOT_FOUND", "Staff member not found in this business.");
            }
            response.StaffName = focus.FullName;
        }

        if (eligible.Count == 0)
        {
            response.NoticeCode = "NO_STAFF_FOR_SERVICES";
            response.Notice = "No staff member is set up to perform all of the selected services yet.";
            response.Days = BuildEmptyDays(startDate, endDate);
            return ApiResponse<AvailabilityCalendarResponse>.Ok(response);
        }

        // Preload shifts + busy time for the whole window in two queries.
        var staffIds = eligible.Select(u => u.Id).ToList();

        var shifts = await _context.StaffShifts.AsNoTracking()
            .Where(s => s.BusinessId == businessId && staffIds.Contains(s.StaffUserId)
                && s.Date >= startDate && s.Date <= endDate)
            .ToListAsync();

        var windowsByStaffDate = shifts
            .GroupBy(s => (s.StaffUserId, s.Date))
            .ToDictionary(
                g => g.Key,
                g => g.Where(s => s.IsWorking && s.EndHour > s.StartHour)
                      .Select(s => (Start: s.StartHour * 60, End: s.EndHour * 60))
                      .OrderBy(w => w.Start)
                      .ToList());

        var rangeStartUtc = ToUtc(startDate.ToDateTime(TimeOnly.MinValue), tz);
        var rangeEndUtc = ToUtc(endDate.AddDays(1).ToDateTime(TimeOnly.MinValue), tz);

        var busyRows = await _context.Appointments.AsNoTracking()
            .Where(a => a.BusinessId == businessId
                && a.StaffUserId != null && staffIds.Contains(a.StaffUserId.Value)
                && a.Status != "cancelled"
                && a.ScheduledAt < rangeEndUtc && a.EndAt > rangeStartUtc)
            .Select(a => new { StaffUserId = a.StaffUserId!.Value, a.ScheduledAt, a.EndAt })
            .ToListAsync();

        var busyByStaff = busyRows
            .GroupBy(b => b.StaffUserId)
            .ToDictionary(g => g.Key, g => g.Select(b => (Start: b.ScheduledAt, End: b.EndAt)).ToList());

        var defaultWindow = (Start: business.BookingOpenHour * 60, End: business.BookingCloseHour * 60);
        var earliestBookableUtc = nowUtc.AddMinutes(leadMinutes);

        for (var date = startDate; date <= endDate; date = date.AddDays(1))
        {
            response.Days.Add(BuildDay(
                date, tz, eligible, windowsByStaffDate, busyByStaff, defaultWindow,
                interval, totalMinutes, distinctRequested, staffUserId, earliestBookableUtc));
        }

        if (response.Days.All(d => d.AvailableSlots == 0))
        {
            response.NoticeCode = staffUserId.HasValue ? "STAFF_FULLY_BOOKED" : "NO_OPENINGS";
            response.Notice = staffUserId.HasValue
                ? $"{response.StaffName} has no openings in this period."
                : "There are no openings in this period. Try a later date.";
        }

        return ApiResponse<AvailabilityCalendarResponse>.Ok(response);
    }

    /// <summary>
    /// Validates a concrete booking time against the grid and resolves which staff
    /// member will take it. When staffUserId is null an available eligible staff
    /// member is auto-assigned (least loaded that day), so an "any available"
    /// booking never lands as an unassigned appointment that blocks nobody.
    /// </summary>
    public async Task<SlotResolution> ResolveBookingStaffAsync(
        Guid businessId,
        Guid[] serviceIds,
        Guid? staffUserId,
        DateTime scheduledAtUtc,
        int totalMinutes,
        Guid? excludeAppointmentId,
        bool enforceLeadTime)
    {
        if (totalMinutes <= 0)
            return SlotResolution.Deny("VALIDATION_ERROR", "The selected services have no duration configured.");

        var business = await _context.Businesses.AsNoTracking().FirstOrDefaultAsync(b => b.Id == businessId);
        if (business == null)
            return SlotResolution.Deny("NOT_FOUND", "Business not found.");

        var startUtc = DateTime.SpecifyKind(scheduledAtUtc, DateTimeKind.Utc);
        var endUtc = startUtc.AddMinutes(totalMinutes);

        if (enforceLeadTime && startUtc < DateTime.UtcNow.AddMinutes(Math.Max(0, business.BookingLeadTimeMinutes)))
            return SlotResolution.Deny("SLOT_UNAVAILABLE", "That time has already passed or is too soon to book.");

        var tz = ResolveTimeZone(business.TimeZoneId);
        var local = TimeZoneInfo.ConvertTimeFromUtc(startUtc, tz);
        var localDate = DateOnly.FromDateTime(local);
        var startMinute = (int)local.TimeOfDay.TotalMinutes;

        var distinctRequested = serviceIds.Distinct().ToArray();
        var eligible = await GetEligibleStaffAsync(businessId, distinctRequested);
        if (eligible.Count == 0)
            return SlotResolution.Deny("STAFF_NOT_AVAILABLE", "No staff member performs all of the selected services.");

        if (staffUserId.HasValue)
        {
            eligible = eligible.Where(u => u.Id == staffUserId.Value).ToList();
            if (eligible.Count == 0)
                return SlotResolution.Deny("STAFF_NOT_AVAILABLE", "Staff member is not available for all requested services.");
        }

        var staffIds = eligible.Select(u => u.Id).ToList();

        var dayShifts = await _context.StaffShifts.AsNoTracking()
            .Where(s => s.BusinessId == businessId && staffIds.Contains(s.StaffUserId) && s.Date == localDate)
            .ToListAsync();

        var dayStartUtc = ToUtc(localDate.ToDateTime(TimeOnly.MinValue), tz);
        var dayEndUtc = ToUtc(localDate.AddDays(1).ToDateTime(TimeOnly.MinValue), tz);

        var dayBusy = await _context.Appointments.AsNoTracking()
            .Where(a => a.BusinessId == businessId
                && a.StaffUserId != null && staffIds.Contains(a.StaffUserId.Value)
                && a.Status != "cancelled"
                && (excludeAppointmentId == null || a.Id != excludeAppointmentId)
                && a.ScheduledAt < dayEndUtc && a.EndAt > dayStartUtc)
            .Select(a => new { StaffUserId = a.StaffUserId!.Value, a.ScheduledAt, a.EndAt })
            .ToListAsync();

        var defaultWindow = (Start: business.BookingOpenHour * 60, End: business.BookingCloseHour * 60);

        Guid? bestStaff = null;
        var bestLoad = int.MaxValue;
        var sawOffDuty = false;
        var sawBooked = false;

        foreach (var staff in eligible)
        {
            var windows = ResolveShiftWindows(dayShifts.Where(s => s.StaffUserId == staff.Id).ToList(), defaultWindow);
            if (!windows.Any(w => startMinute >= w.Start && startMinute + totalMinutes <= w.End))
            {
                sawOffDuty = true;
                continue;
            }

            var busy = dayBusy.Where(b => b.StaffUserId == staff.Id).ToList();
            if (busy.Any(b => b.ScheduledAt < endUtc && b.EndAt > startUtc))
            {
                sawBooked = true;
                continue;
            }

            if (busy.Count < bestLoad)
            {
                bestLoad = busy.Count;
                bestStaff = staff.Id;
            }
        }

        if (bestStaff != null)
            return SlotResolution.Allow(bestStaff);
        if (sawBooked)
            return SlotResolution.Deny("OVERBOOKING", "That slot has just been taken. Please pick another time.");
        if (sawOffDuty)
            return SlotResolution.Deny("SLOT_UNAVAILABLE", "Nobody is working at that time. Please pick another slot.");

        return SlotResolution.Deny("SLOT_UNAVAILABLE", "That slot is not bookable.");
    }

    // ===========================================================
    //  GRID CONSTRUCTION
    // ===========================================================

    private DayAvailabilityResponse BuildDay(
        DateOnly date,
        TimeZoneInfo tz,
        List<User> eligible,
        Dictionary<(Guid StaffUserId, DateOnly Date), List<(int Start, int End)>> windowsByStaffDate,
        Dictionary<Guid, List<(DateTime Start, DateTime End)>> busyByStaff,
        (int Start, int End) defaultWindow,
        int interval,
        int totalMinutes,
        Guid[] serviceIds,
        Guid? focusStaffId,
        DateTime earliestBookableUtc)
    {
        var day = NewDay(date);

        // Per-staff working windows for this date: shift rows win, else the business default.
        var windows = new Dictionary<Guid, List<(int Start, int End)>>();
        foreach (var staff in eligible)
        {
            windows[staff.Id] = windowsByStaffDate.TryGetValue((staff.Id, date), out var rostered)
                ? rostered
                : new List<(int, int)> { defaultWindow };
        }

        day.IsOpen = windows.Values.Any(w => w.Count > 0);

        // Grid bounds span every working window so off-duty times still render.
        var gridStart = defaultWindow.Start;
        var gridEnd = defaultWindow.End;
        foreach (var w in windows.Values.SelectMany(x => x))
        {
            gridStart = Math.Min(gridStart, w.Start);
            gridEnd = Math.Max(gridEnd, w.End);
        }

        var dayStartUtc = ToUtc(date.ToDateTime(TimeOnly.MinValue), tz);
        var dayEndUtc = ToUtc(date.AddDays(1).ToDateTime(TimeOnly.MinValue), tz);

        var dayBusy = new Dictionary<Guid, List<(DateTime Start, DateTime End)>>();
        foreach (var staff in eligible)
        {
            dayBusy[staff.Id] = busyByStaff.TryGetValue(staff.Id, out var all)
                ? all.Where(b => b.Start < dayEndUtc && b.End > dayStartUtc).ToList()
                : new List<(DateTime, DateTime)>();
        }

        var staffById = eligible.ToDictionary(s => s.Id);
        var suggestions = eligible.ToDictionary(
            s => s.Id,
            s => new StaffAvailabilitySuggestion { StaffUserId = s.Id, StaffName = s.FullName, AvatarUrl = s.AvatarUrl });

        for (var minute = gridStart; minute + totalMinutes <= gridEnd; minute += interval)
        {
            var localStart = date.ToDateTime(TimeOnly.MinValue).AddMinutes(minute);
            if (tz.IsInvalidTime(DateTime.SpecifyKind(localStart, DateTimeKind.Unspecified)))
                continue; // DST spring-forward gap

            var startUtc = ToUtc(localStart, tz);
            var endUtc = startUtc.AddMinutes(totalMinutes);
            var isPast = startUtc < earliestBookableUtc;

            var free = new List<Guid>();
            var blocked = false;

            foreach (var staff in eligible)
            {
                var onDuty = windows[staff.Id].Any(w => minute >= w.Start && minute + totalMinutes <= w.End);
                if (!onDuty) continue;

                if (dayBusy[staff.Id].Any(b => b.Start < endUtc && b.End > startUtc))
                {
                    blocked = true;
                    continue;
                }

                free.Add(staff.Id);

                if (!isPast && staff.Id != focusStaffId)
                {
                    var suggestion = suggestions[staff.Id];
                    suggestion.AvailableSlotCount++;
                    if (suggestion.NextAvailableUtc == null)
                    {
                        suggestion.NextAvailableUtc = startUtc;
                        suggestion.NextAvailableLocalTime = localStart.ToString("HH:mm", CultureInfo.InvariantCulture);
                    }
                }
            }

            var slot = new AvailabilitySlotResponse
            {
                StartAtUtc = startUtc,
                EndAtUtc = endUtc,
                ServiceIds = serviceIds,
                LocalDate = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                LocalTime = localStart.ToString("HH:mm", CultureInfo.InvariantCulture),
                DayPart = DayPartOf(localStart.Hour)
            };

            if (focusStaffId.HasValue)
            {
                var isFree = free.Contains(focusStaffId.Value);
                var onDuty = windows[focusStaffId.Value]
                    .Any(w => minute >= w.Start && minute + totalMinutes <= w.End);

                slot.Status = isPast ? SlotStatuses.Past
                    : isFree ? SlotStatuses.Available
                    : onDuty ? SlotStatuses.Booked
                    : SlotStatuses.OffDuty;

                slot.StaffUserId = focusStaffId;
                slot.StaffName = staffById[focusStaffId.Value].FullName;
            }
            else
            {
                // "Any available": hand the slot to the least-loaded free staff member.
                var assigned = free
                    .OrderBy(id => dayBusy[id].Count)
                    .ThenBy(id => staffById[id].FullName)
                    .ThenBy(id => id)
                    .Select(id => (Guid?)id)
                    .FirstOrDefault();

                slot.Status = isPast ? SlotStatuses.Past
                    : assigned != null ? SlotStatuses.Available
                    : blocked ? SlotStatuses.Booked
                    : SlotStatuses.OffDuty;

                slot.StaffUserId = assigned;
                slot.StaffName = assigned != null ? staffById[assigned.Value].FullName : string.Empty;
            }

            slot.IsAvailable = slot.Status == SlotStatuses.Available;

            if (!slot.IsAvailable && !isPast)
            {
                slot.AlternativeStaff = free
                    .Where(id => id != focusStaffId)
                    .Take(3)
                    .Select(id => new AvailabilityStaffRef
                    {
                        StaffUserId = id,
                        StaffName = staffById[id].FullName,
                        AvatarUrl = staffById[id].AvatarUrl
                    })
                    .ToList();
            }

            day.Slots.Add(slot);
            day.TotalSlots++;
            if (slot.IsAvailable)
            {
                day.AvailableSlots++;
                day.FirstAvailableUtc ??= startUtc;
            }
        }

        day.Suggestions = suggestions.Values
            .Where(s => s.AvailableSlotCount > 0 && s.StaffUserId != focusStaffId)
            .OrderByDescending(s => s.AvailableSlotCount)
            .ThenBy(s => s.NextAvailableUtc)
            .Take(MaxSuggestions)
            .ToList();

        // Without a staff filter the suggestions just restate the grid; they only
        // earn their place once the customer's own pick has run out of openings.
        if (!focusStaffId.HasValue && day.AvailableSlots > 0)
            day.Suggestions.Clear();

        return day;
    }

    private static List<DayAvailabilityResponse> BuildEmptyDays(DateOnly startDate, DateOnly endDate)
    {
        var days = new List<DayAvailabilityResponse>();
        for (var date = startDate; date <= endDate; date = date.AddDays(1))
            days.Add(NewDay(date));
        return days;
    }

    private static DayAvailabilityResponse NewDay(DateOnly date) => new()
    {
        Date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        Weekday = date.ToString("ddd", CultureInfo.InvariantCulture),
        DayOfMonth = date.Day
    };

    // ===========================================================
    //  HELPERS
    // ===========================================================

    /// <summary>Staff of the business assigned to every requested service.</summary>
    private async Task<List<User>> GetEligibleStaffAsync(Guid businessId, Guid[] serviceIds)
    {
        var assignments = await _context.StaffServiceAssignments.AsNoTracking()
            .Where(a => a.BusinessId == businessId && serviceIds.Contains(a.ServiceCatalogItemId))
            .Select(a => new { a.StaffUserId, a.ServiceCatalogItemId })
            .ToListAsync();

        var staffIds = assignments
            .GroupBy(a => a.StaffUserId)
            .Where(g => g.Select(x => x.ServiceCatalogItemId).Distinct().Count() == serviceIds.Length)
            .Select(g => g.Key)
            .ToList();

        if (staffIds.Count == 0)
            return new List<User>();

        return await _context.Users.AsNoTracking()
            .Where(u => staffIds.Contains(u.Id) && u.StaffBusinessId == businessId)
            .OrderBy(u => u.FullName)
            .ToListAsync();
    }

    /// <summary>
    /// A staff member's working windows for one date: rostered shifts when the day
    /// has any shift row, otherwise the business default window. A date rostered
    /// entirely as not-working yields no windows (explicit day off).
    /// </summary>
    private static List<(int Start, int End)> ResolveShiftWindows(
        List<StaffShift> rostered, (int Start, int End) defaultWindow)
    {
        if (rostered.Count == 0)
            return new List<(int, int)> { defaultWindow };

        return rostered
            .Where(s => s.IsWorking && s.EndHour > s.StartHour)
            .Select(s => (Start: s.StartHour * 60, End: s.EndHour * 60))
            .OrderBy(w => w.Start)
            .ToList();
    }

    private static string DayPartOf(int hour) =>
        hour < 12 ? "morning" : hour < 17 ? "afternoon" : "evening";

    private static int NormalizeInterval(int interval) =>
        interval < 5 ? 15 : interval > 240 ? 240 : interval;

    private static DateTime ToUtc(DateTime local, TimeZoneInfo tz)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (tz.IsInvalidTime(unspecified))
            unspecified = unspecified.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(unspecified, tz);
    }

    /// <summary>Resolves an IANA/Windows zone id, falling back to UTC when unknown.</summary>
    private TimeZoneInfo ResolveTimeZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return TimeZoneInfo.Utc;

        return TimeZoneCache.GetOrAdd(id, key =>
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(key);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                _logger.LogWarning("Unknown business timezone '{TimeZoneId}'; falling back to UTC.", key);
                return TimeZoneInfo.Utc;
            }
        });
    }

    private static ApiResponse<AvailabilityCalendarResponse> Fail(string code, string message) =>
        ApiResponse<AvailabilityCalendarResponse>.Fail(code, message);
}