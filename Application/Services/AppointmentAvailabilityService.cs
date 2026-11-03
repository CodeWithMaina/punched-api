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

    /// <summary>
    /// The clock every availability decision reads. Injectable so a test can pin
    /// "now" and assert lead time, the past-slot boundary and the booking window
    /// without depending on the wall clock (which is what made the reschedule
    /// tests pass in the morning and fail in the evening).
    /// </summary>
    private readonly TimeProvider _time;

    public AppointmentAvailabilityService(
        ApplicationDbContext context,
        ILogger<AppointmentAvailabilityService> logger,
        TimeProvider? timeProvider = null)
    {
        _context = context;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Current UTC time, from the injected clock.</summary>
    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

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
        var nowUtc = UtcNow;

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
                var existsInBusiness = await IsMemberOfBusinessAsync(staffUserId.Value, businessId);
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

        // Preload rostered days + the recurring week for the whole window.
        var staffIds = eligible.Select(u => u.Id).ToList();

        var shifts = await _context.StaffShifts.AsNoTracking()
            .Where(s => s.BusinessId == businessId && staffIds.Contains(s.StaffUserId)
                && s.Date >= startDate && s.Date <= endDate)
            .ToListAsync();

        var weekly = await _context.StaffAvailability.AsNoTracking()
            .Where(a => a.BusinessId == businessId && staffIds.Contains(a.StaffUserId))
            .ToListAsync();

        var shiftsByStaffDate = shifts
            .GroupBy(s => (s.StaffUserId, s.Date))
            .ToDictionary(g => g.Key, g => g.ToList());

        var weeklyByStaff = weekly
            .GroupBy(a => a.StaffUserId)
            .ToDictionary(g => g.Key, g => g.ToDictionary(a => a.DayOfWeek));

        var rangeStartUtc = ToUtc(startDate.ToDateTime(TimeOnly.MinValue), tz);
        var rangeEndUtc = ToUtc(endDate.AddDays(1).ToDateTime(TimeOnly.MinValue), tz);

        // Only statuses that actually hold the staff member's time are busy; a
        // cancelled, completed or no-show appointment has released its interval.
        var reservingStatuses = AppointmentReservations.ReservingStatuses.ToArray();

        var busyRows = await _context.Appointments.AsNoTracking()
            .Where(a => a.BusinessId == businessId
                && a.StaffUserId != null && staffIds.Contains(a.StaffUserId.Value)
                && reservingStatuses.Contains(a.Status)
                && a.ScheduledAt < rangeEndUtc && a.EndAt > rangeStartUtc)
            .Select(a => new { StaffUserId = a.StaffUserId!.Value, a.ScheduledAt, a.EndAt })
            .ToListAsync();

        var busyByStaff = busyRows
            .GroupBy(b => b.StaffUserId)
            .ToDictionary(g => g.Key, g => g.Select(b => (Start: b.ScheduledAt, End: b.EndAt)).ToList());

        var defaultWindow = (Start: business.BookingOpenHour * 60, End: business.BookingCloseHour * 60);
        var earliestBookableUtc = nowUtc.AddMinutes(leadMinutes);

        // Resolve every (staff, date) window once: a dated row for the date wins,
        // else the recurring weekday, else the business's own booking window.
        var windowsByStaffDate = new Dictionary<(Guid, DateOnly), List<(int Start, int End)>>();
        for (var date = startDate; date <= endDate; date = date.AddDays(1))
        {
            foreach (var staffId in staffIds)
            {
                shiftsByStaffDate.TryGetValue((staffId, date), out var rostered);
                weeklyByStaff.TryGetValue(staffId, out var pattern);
                windowsByStaffDate[(staffId, date)] =
                    ResolveWindows(date, rostered, pattern, defaultWindow);
            }
        }

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

        if (enforceLeadTime && startUtc < UtcNow.AddMinutes(Math.Max(0, business.BookingLeadTimeMinutes)))
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

        var weekly = await _context.StaffAvailability.AsNoTracking()
            .Where(a => a.BusinessId == businessId && staffIds.Contains(a.StaffUserId))
            .ToListAsync();

        var weeklyByStaff = weekly
            .GroupBy(a => a.StaffUserId)
            .ToDictionary(g => g.Key, g => g.ToDictionary(a => a.DayOfWeek));

        var dayStartUtc = ToUtc(localDate.ToDateTime(TimeOnly.MinValue), tz);
        var dayEndUtc = ToUtc(localDate.AddDays(1).ToDateTime(TimeOnly.MinValue), tz);

        var reservingStatuses = AppointmentReservations.ReservingStatuses.ToArray();

        var dayBusy = await _context.Appointments.AsNoTracking()
            .Where(a => a.BusinessId == businessId
                && a.StaffUserId != null && staffIds.Contains(a.StaffUserId.Value)
                && reservingStatuses.Contains(a.Status)
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
            weeklyByStaff.TryGetValue(staff.Id, out var pattern);
            var windows = ResolveWindows(
                localDate,
                dayShifts.Where(s => s.StaffUserId == staff.Id).ToList(),
                pattern,
                defaultWindow);
            if (!windows.Any(w => startMinute >= w.Start && startMinute + totalMinutes <= w.End))
            {
                sawOffDuty = true;
                continue;
            }

            var busy = dayBusy.Where(b => b.StaffUserId == staff.Id).ToList();
            if (busy.Any(b => AppointmentReservations.Overlaps(b.ScheduledAt, b.EndAt, startUtc, endUtc)))
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
            return SlotResolution.Deny(
                AppointmentConflicts.StaffTimeUnavailable, AppointmentConflicts.StaffTimeUnavailableMessage);
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

                if (dayBusy[staff.Id].Any(b => AppointmentReservations.Overlaps(b.Start, b.End, startUtc, endUtc)))
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

    /// <summary>True when the business has at least one ACTIVE staff membership.</summary>
    private Task<bool> HasActiveStaffAsync(Guid businessId) =>
        _context.BusinessMemberships.AsNoTracking().AnyAsync(m =>
            m.BusinessId == businessId &&
            m.Role == BusinessMembershipRole.Staff &&
            m.Status == BusinessMembershipStatus.Active);

    /// <summary>True when the identity holds any ACTIVE membership of the business.</summary>
    private Task<bool> IsMemberOfBusinessAsync(Guid userId, Guid businessId) =>
        _context.BusinessMemberships.AsNoTracking().AnyAsync(m =>
            m.UserId == userId &&
            m.BusinessId == businessId &&
            m.Status == BusinessMembershipStatus.Active);

    /// <summary>
    /// True when the user owns a business that has no linked staff — the owner
    /// is then the sole provider and their diary is the one bookings block.
    /// </summary>
    public async Task<bool> IsSoloOwnerAsync(Guid businessId, Guid userId) =>
        await _context.BusinessMemberships.AsNoTracking().AnyAsync(m =>
            m.BusinessId == businessId &&
            m.UserId == userId &&
            m.Role == BusinessMembershipRole.Owner &&
            m.Status == BusinessMembershipStatus.Active)
        && !await HasActiveStaffAsync(businessId);

    /// <summary>
    /// Staff of the business assigned to every requested service. A business with
    /// no linked staff falls back to its owner as the single provider.
    /// </summary>
    private async Task<List<User>> GetEligibleStaffAsync(Guid businessId, Guid[] serviceIds)
    {
        var hasStaff = await HasActiveStaffAsync(businessId);
        if (!hasStaff)
        {
            var ownerId = await _context.BusinessMemberships.AsNoTracking()
                .Where(m => m.BusinessId == businessId &&
                            m.Role == BusinessMembershipRole.Owner &&
                            m.Status == BusinessMembershipStatus.Active)
                .Select(m => (Guid?)m.UserId)
                .FirstOrDefaultAsync();
            if (ownerId == null || ownerId == Guid.Empty)
                return new List<User>();

            return await _context.Users.AsNoTracking()
                .Where(u => u.Id == ownerId)
                .ToListAsync();
        }

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
            .Where(u => staffIds.Contains(u.Id) && _context.BusinessMemberships.Any(m =>
                m.UserId == u.Id &&
                m.BusinessId == businessId &&
                m.Role == BusinessMembershipRole.Staff &&
                m.Status == BusinessMembershipStatus.Active &&
                m.IsBookable))
            .OrderBy(u => u.FullName)
            .ToListAsync();
    }

    /// <summary>
    /// A staff member's working windows for one date, in minutes from local
    /// midnight.
    ///
    /// <para>Resolution order is deliberate and must stay in this order:</para>
    /// <list type="number">
    /// <item>any DATED shift rows for the date — an override or a day off wins;</item>
    /// <item>otherwise the RECURRING weekday pattern;</item>
    /// <item>otherwise the business's own booking window, which is how the system
    /// behaved before staff availability existed.</item>
    /// </list>
    ///
    /// <para>A configured break splits the window in two, so a booking can never
    /// be placed across it.</para>
    /// </summary>
    private static List<(int Start, int End)> ResolveWindows(
        DateOnly date,
        List<StaffShift>? rostered,
        Dictionary<int, StaffAvailability>? weekly,
        (int Start, int End) defaultWindow)
    {
        List<(bool Working, int Start, int End, int? BreakStart, int? BreakEnd)> source;

        if (rostered is { Count: > 0 })
        {
            source = rostered
                .Select(s => (
                    s.IsWorking,
                    s.StartMinute ?? s.StartHour * 60,
                    s.EndMinute ?? s.EndHour * 60,
                    s.BreakStartMinute,
                    s.BreakEndMinute))
                .ToList();
        }
        else if (weekly != null && weekly.TryGetValue((int)date.DayOfWeek, out var pattern))
        {
            source = new List<(bool, int, int, int?, int?)>
            {
                (pattern.IsWorking, pattern.StartMinute, pattern.EndMinute,
                    pattern.BreakStartMinute, pattern.BreakEndMinute)
            };
        }
        else
        {
            source = new List<(bool, int, int, int?, int?)>
            {
                (true, defaultWindow.Start, defaultWindow.End, null, null)
            };
        }

        var windows = new List<(int Start, int End)>();

        foreach (var (working, start, end, breakStart, breakEnd) in source)
        {
            if (!working || end <= start) continue;

            var hasBreak = breakStart.HasValue && breakEnd.HasValue
                && breakStart.Value >= start && breakEnd.Value <= end
                && breakEnd.Value > breakStart.Value;

            if (!hasBreak)
            {
                windows.Add((start, end));
                continue;
            }

            if (breakStart!.Value > start) windows.Add((start, breakStart.Value));
            if (breakEnd!.Value < end) windows.Add((breakEnd.Value, end));
        }

        return windows.OrderBy(w => w.Start).ToList();
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