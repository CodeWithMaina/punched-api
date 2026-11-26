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
    /// The authoritative provider-eligibility resolver. Optional in the
    /// constructor so existing construction sites (tests) keep compiling; when
    /// absent a resolver over the same DbContext is used, so behaviour is
    /// identical — category-based eligibility, owner-as-provider fallback, etc.
    /// </summary>
    private readonly IProviderEligibilityService _eligibility;

    /// <summary>
    /// The clock every availability decision reads. Injectable so a test can pin
    /// "now" and assert lead time, the past-slot boundary and the booking window
    /// without depending on the wall clock (which is what made the reschedule
    /// tests pass in the morning and fail in the evening).
    /// </summary>
    private readonly TimeProvider _time;

    /// <summary>Business-level operating hours; constrains every staff window.</summary>
    private readonly IBusinessScheduleService _schedule;

    /// <summary>
    /// The one-read loader every grid and booking decision uses. Reading the business,
    /// catalogue, roster and schedule in parallel is what keeps availability responsive
    /// when each round trip costs a couple of hundred milliseconds.
    /// </summary>
    private readonly BookingReadContextLoader _readContexts;

    public AppointmentAvailabilityService(
        ApplicationDbContext context,
        ILogger<AppointmentAvailabilityService> logger,
        TimeProvider? timeProvider = null,
        IProviderEligibilityService? providerEligibility = null,
        IBusinessScheduleService? businessSchedule = null,
        BookingReadContextLoader? readContexts = null)
    {
        _context = context;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
        _eligibility = providerEligibility ?? new ProviderEligibilityService(context);
        _schedule = businessSchedule ?? new BusinessScheduleService(context);
        _readContexts = readContexts ?? new BookingReadContextLoader(context);
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

        var distinctRequested = serviceIds.Distinct().ToArray();

        // Everything the grid needs — the business, its catalogue, the eligible roster and
        // every schedule row — is read in ONE parallel wave (BookingReadContextLoader).
        // Used to be a dozen sequential round trips, which on a remote database was the
        // difference between a responsive date picker and a four-second wait.
        var provisionalStart = DateOnly.FromDateTime(UtcNow).AddDays(-1);
        var provisionalEnd = provisionalStart.AddDays(MaxRangeDays + 2);

        var read = await _readContexts.LoadAsync(
            businessId, distinctRequested, provisionalStart, provisionalEnd);

        if (read == null)
            return Fail("NOT_FOUND", "Business not found.");

        var business = read.Business;
        var tz = ResolveTimeZone(business.TimeZoneId);
        var nowUtc = UtcNow;

        // Clamp the window: never before today (business-local), never wider than a month.
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz));
        if (startDate == default || startDate < today) startDate = today;
        if (endDate < startDate) endDate = startDate;
        if (endDate.DayNumber - startDate.DayNumber >= MaxRangeDays)
            endDate = startDate.AddDays(MaxRangeDays - 1);

        var services = read.Services.ToList();
        if (services.Count != distinctRequested.Length)
            return Fail("SERVICE_NOT_FOUND", "One or more services were not found in this business.");

        var totalMinutes = services.Sum(s => s.DurationMinutes);
        if (totalMinutes <= 0)
            return Fail("VALIDATION_ERROR", "The selected services have no duration configured. Ask the business to set a service duration.");

        var interval = NormalizeInterval(business.BookingSlotIntervalMinutes);
        var leadMinutes = Math.Max(0, business.BookingLeadTimeMinutes);

        // The business's own schedule. Empty means "never configured", in which case
        // the legacy booking window applies exactly as before.
        var businessWindows = read.BusinessWindows;
        var scheduleConfigured = read.ScheduleConfigured;

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

        // Eligible roster: members assigned to every requested service, already resolved
        // from the bulk read (owner-based models collapse to the owner).
        var eligible = read.Providers.ToList();

        // Explicit selection is only honoured when it is a product surface. With the Staff
        // module off (or an owner-only model) the provider is allocated automatically, so a
        // hand-edited request naming a *member* must not steer the allocation.
        //
        // A stranger's id is still a 404: ignoring it would hide a cross-tenant probe, and
        // the caller deserves the same answer as any other unknown provider.
        var selectionAllowed = ProviderEligibilityPolicy.AllowsExplicitProviderSelection(read.ProviderModel);
        if (staffUserId.HasValue && !selectionAllowed)
        {
            if (!await IsMemberOfBusinessAsync(staffUserId.Value, businessId))
                return Fail("STAFF_NOT_FOUND", "Staff member not found in this business.");

            _logger.LogInformation(
                "Ignoring an explicit staff selection for business {BusinessId}: provider model '{Model}' allocates automatically.",
                businessId, read.ProviderModel);
            staffUserId = null;
        }

        // The echo reflects the allocation actually used, never the ignored request.
        response.StaffUserId = staffUserId;

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

        // Rosters, recurring weeks, busy intervals and every (staff, date) window were
        // resolved from the single read wave; nothing below asks the database again.
        var busyByStaff = read.BusyByStaff.ToDictionary(
            entry => entry.Key,
            entry => entry.Value.Select(b => (Start: b.StartUtc, End: b.EndUtc)).ToList());

        var defaultWindow = (Start: business.BookingOpenHour * 60, End: business.BookingCloseHour * 60);
        var earliestBookableUtc = nowUtc.AddMinutes(leadMinutes);

        var windowsByStaffDate = new Dictionary<(Guid, DateOnly), List<(int Start, int End)>>();
        for (var date = startDate; date <= endDate; date = date.AddDays(1))
        {
            foreach (var staff in eligible)
                windowsByStaffDate[(staff.Id, date)] = read.WindowsFor(staff.Id, date);
        }

        for (var date = startDate; date <= endDate; date = date.AddDays(1))
        {
            IReadOnlyList<(int Start, int End)>? dayWindows = null;
            if (scheduleConfigured)
                dayWindows = businessWindows.TryGetValue(date, out var scheduledDay)
                    ? scheduledDay
                    : new List<(int, int)>();

            response.Days.Add(BuildDay(
                date, tz, eligible, windowsByStaffDate, busyByStaff, defaultWindow, dayWindows,
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
        bool enforceLeadTime,
        Business? business = null,
        Guid? excludeHoldId = null)
    {
        if (totalMinutes <= 0)
            return SlotResolution.Deny("VALIDATION_ERROR", "The selected services have no duration configured.");

        var startUtc = DateTime.SpecifyKind(scheduledAtUtc, DateTimeKind.Utc);
        var endUtc = startUtc.AddMinutes(totalMinutes);

        // One wide read (UTC date ±1 day covers any business timezone) returns the roster,
        // the resolved working windows for every candidate and the intervals already held.
        var utcDate = DateOnly.FromDateTime(startUtc);
        var read = await _readContexts.LoadAsync(
            businessId, serviceIds, utcDate.AddDays(-1), utcDate.AddDays(1));

        if (read == null)
            return SlotResolution.Deny("NOT_FOUND", "Business not found.");

        business ??= read.Business;

        if (enforceLeadTime && startUtc < UtcNow.AddMinutes(Math.Max(0, business.BookingLeadTimeMinutes)))
            return SlotResolution.Deny("SLOT_UNAVAILABLE", "That time has already passed or is too soon to book.");

        var tz = ResolveTimeZone(business.TimeZoneId);
        var local = TimeZoneInfo.ConvertTimeFromUtc(startUtc, tz);
        var localDate = DateOnly.FromDateTime(local);
        var startMinute = (int)local.TimeOfDay.TotalMinutes;

        var eligible = read.Providers.ToList();
        if (eligible.Count == 0)
            return SlotResolution.Deny("STAFF_NOT_AVAILABLE", "No staff member performs all of the selected services.");

        if (staffUserId.HasValue)
        {
            eligible = eligible.Where(u => u.Id == staffUserId.Value).ToList();
            if (eligible.Count == 0)
                return SlotResolution.Deny("STAFF_NOT_AVAILABLE", "Staff member is not available for all requested services.");
        }

        Guid? bestStaff = null;
        var bestLoad = int.MaxValue;
        var sawOffDuty = false;
        var sawBooked = false;

        foreach (var staff in eligible)
        {
            var windows = read.WindowsFor(staff.Id, localDate);

            if (!windows.Any(w => startMinute >= w.Start && startMinute + totalMinutes <= w.End))
            {
                sawOffDuty = true;
                continue;
            }

            var busy = read.BusyFor(staff.Id)
                .Where(b => excludeAppointmentId == null || b.AppointmentId != excludeAppointmentId)
                // A hold being converted must not block the booking it is becoming.
                .Where(b => excludeHoldId == null || b.HoldId != excludeHoldId)
                .ToList();

            if (busy.Any(b => AppointmentReservations.Overlaps(b.StartUtc, b.EndUtc, startUtc, endUtc)))
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
        IReadOnlyList<(int Start, int End)>? businessWindows,
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
                : businessWindows is { Count: > 0 }
                    ? businessWindows.ToList()
                    : new List<(int, int)> { defaultWindow };
        }

        day.IsOpen = windows.Values.Any(w => w.Count > 0);

        // Grid bounds span every working window so off-duty times still render.
        var gridStart = businessWindows is { Count: > 0 } ? businessWindows.Min(w => w.Start) : defaultWindow.Start;
        var gridEnd = businessWindows is { Count: > 0 } ? businessWindows.Max(w => w.End) : defaultWindow.End;
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

    /// <summary>True when the identity holds any ACTIVE membership of the business.</summary>
    private Task<bool> IsMemberOfBusinessAsync(Guid userId, Guid businessId) =>
        _context.BusinessMemberships.AsNoTracking().AnyAsync(m =>
            m.UserId == userId &&
            m.BusinessId == businessId &&
            m.Status == BusinessMembershipStatus.Active);


    /// <summary>
    /// Whether <paramref name="providerUserId"/> is eligible to take all of the
    /// requested services, resolved through the single eligibility mechanism.
    /// The authoritative server-side check behind booking and reschedule, so the
    /// appointment service never re-implements the rule.
    /// </summary>
    public Task<bool> IsProviderEligibleAsync(Guid businessId, Guid providerUserId, Guid[] serviceIds) =>
        _eligibility.IsEligibleAsync(businessId, providerUserId, serviceIds);

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
