using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace PunchedApi.Application.DTOs;

/// <summary>
/// One staff member's full management profile: identity, employment, storefront
/// settings, assigned services, working week and time off.
///
/// <para>This is a READ shape for the owner's staff profile page. Everything that
/// changes per visit (bookings, attendance events, commission ledger) is served by
/// the module that owns it — appointments, attendance and commissions — so this
/// payload stays cacheable and never becomes a second source of truth for
/// another module's numbers.</para>
/// </summary>
public class StaffDetailResponse
{
    // ── Identity (the User row) ───────────────────────────────
    [JsonPropertyName("userId")] public Guid UserId { get; set; }
    [JsonPropertyName("fullName")] public string FullName { get; set; } = string.Empty;
    [JsonPropertyName("email")] public string Email { get; set; } = string.Empty;
    [JsonPropertyName("phoneNumber")] public string? PhoneNumber { get; set; }
    [JsonPropertyName("avatarUrl")] public string? AvatarUrl { get; set; }
    [JsonPropertyName("avatarMediaId")] public Guid? AvatarMediaId { get; set; }

    // ── Employment (the BusinessMembership row) ───────────────
    /// <summary><c>Active</c> | <c>Invited</c> | <c>Suspended</c> | <c>Left</c>.</summary>
    [JsonPropertyName("membershipStatus")] public string MembershipStatus { get; set; } = "Active";
    /// <summary>True only for an <c>Active</c> membership — the flag every action gates on.</summary>
    [JsonPropertyName("isActive")] public bool IsActive { get; set; }
    [JsonPropertyName("joinedAt")] public DateTime JoinedAt { get; set; }
    [JsonPropertyName("endedAt")] public DateTime? EndedAt { get; set; }

    [JsonPropertyName("positionId")] public Guid? PositionId { get; set; }
    [JsonPropertyName("positionName")] public string? PositionName { get; set; }

    // ── Storefront profile of this member inside this business ─
    [JsonPropertyName("bio")] public string? Bio { get; set; }
    [JsonPropertyName("specialities")] public string? Specialities { get; set; }
    [JsonPropertyName("yearsOfExperience")] public int? YearsOfExperience { get; set; }
    [JsonPropertyName("isPublic")] public bool IsPublic { get; set; }
    [JsonPropertyName("isBookable")] public bool IsBookable { get; set; }

    // ── Goals ────────────────────────────────────────────────
    [JsonPropertyName("dailyGoalOverride")] public int? DailyGoalOverride { get; set; }
    [JsonPropertyName("appointmentDailyGoalOverride")] public int? AppointmentDailyGoalOverride { get; set; }
    [JsonPropertyName("dailyGoal")] public int? DailyGoal { get; set; }
    [JsonPropertyName("appointmentDailyGoal")] public int? AppointmentDailyGoal { get; set; }
    /// <summary>Which metric the business tracks: <c>stamps</c> | <c>appointments</c>.</summary>
    [JsonPropertyName("dailyGoalType")] public string DailyGoalType { get; set; } = "stamps";

    // ── Activity snapshot (cheap counters, not module ledgers) ─
    [JsonPropertyName("stampsIssued")] public int StampsIssued { get; set; }
    [JsonPropertyName("stampsToday")] public int StampsToday { get; set; }
    [JsonPropertyName("stampsLast7d")] public int StampsLast7d { get; set; }
    [JsonPropertyName("appointmentsToday")] public int AppointmentsToday { get; set; }
    [JsonPropertyName("appointmentsLast7d")] public int AppointmentsLast7d { get; set; }
    [JsonPropertyName("lastActivityAt")] public DateTime? LastActivityAt { get; set; }

    // ── Relations ────────────────────────────────────────────
    /// <summary>Services this member may deliver, with the price/duration snapshot.</summary>
    [JsonPropertyName("services")] public List<StaffServiceLinkResponse> Services { get; set; } = [];

    /// <summary>
    /// The service categories this provider specialises in — the source of their
    /// service eligibility. Empty for a provider not yet assigned any category.
    /// </summary>
    [JsonPropertyName("categories")] public List<ProviderCategoryResponse> Categories { get; set; } = [];
    /// <summary>The seven recurring weekdays; always seven entries.</summary>
    [JsonPropertyName("availability")] public List<StaffAvailabilityDayResponse> Availability { get; set; } = [];
    /// <summary>
    /// Where the working week above comes from, so the UI can state it instead of
    /// guessing: <c>pattern</c> (a saved recurring week), <c>roster</c> (derived from
    /// the dated shifts already on the calendar) or <c>business</c> (neither exists
    /// yet, so the business's own booking window applies).
    /// </summary>
    [JsonPropertyName("availabilitySource")] public string AvailabilitySource { get; set; } = "business";
    /// <summary>Dated exceptions in the horizon: days off and one-off changes.</summary>
    [JsonPropertyName("timeOff")] public List<StaffTimeOffResponse> TimeOff { get; set; } = [];

    // ── Business booking window, so "using business hours" is explainable ─
    [JsonPropertyName("bookingOpenMinute")] public int BookingOpenMinute { get; set; }
    [JsonPropertyName("bookingCloseMinute")] public int BookingCloseMinute { get; set; }
    [JsonPropertyName("timeZoneId")] public string? TimeZoneId { get; set; }
}

/// <summary>One service a staff member is assigned to deliver.</summary>
public class StaffServiceLinkResponse
{
    [JsonPropertyName("serviceId")] public Guid ServiceId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("categoryId")] public Guid? CategoryId { get; set; }
    [JsonPropertyName("categoryName")] public string? CategoryName { get; set; }
    [JsonPropertyName("durationMinutes")] public int DurationMinutes { get; set; }
    [JsonPropertyName("price")] public decimal? Price { get; set; }
    /// <summary>False when the service is paused/archived: the assignment is kept, but it cannot be booked.</summary>
    [JsonPropertyName("isActive")] public bool IsActive { get; set; }
    [JsonPropertyName("showcase")] public bool Showcase { get; set; }
}

/// <summary>One weekday of the recurring working week.</summary>
public class StaffAvailabilityDayResponse
{
    /// <summary>0 = Sunday … 6 = Saturday.</summary>
    [JsonPropertyName("dayOfWeek")] public int DayOfWeek { get; set; }
    [JsonPropertyName("dayName")] public string DayName { get; set; } = string.Empty;
    [JsonPropertyName("isWorking")] public bool IsWorking { get; set; }
    [JsonPropertyName("startMinute")] public int StartMinute { get; set; }
    [JsonPropertyName("endMinute")] public int EndMinute { get; set; }
    [JsonPropertyName("breakStartMinute")] public int? BreakStartMinute { get; set; }
    [JsonPropertyName("breakEndMinute")] public int? BreakEndMinute { get; set; }
}

/// <summary>A dated exception to the recurring week (a day off, or a one-off change).</summary>
public class StaffTimeOffResponse
{
    [JsonPropertyName("date")] public DateOnly Date { get; set; }
    [JsonPropertyName("isWorking")] public bool IsWorking { get; set; }
    [JsonPropertyName("startMinute")] public int StartMinute { get; set; }
    [JsonPropertyName("endMinute")] public int EndMinute { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
}


/// <summary>
/// Partial update of one staff member. Every field is optional: an omitted field
/// is left untouched, so two screens editing different parts of the same profile
/// can never overwrite each other.
///
/// <para><b>Email is deliberately absent.</b> It is the sign-in identity, and the
/// platform changes it only through the account-verification flow — a business
/// editing it here would silently break the person's login.</para>
/// </summary>
public class UpdateStaffRequest
{
    [JsonPropertyName("fullName")]
    [MaxLength(100)]
    public string? FullName { get; set; }

    [JsonPropertyName("phoneNumber")]
    [MaxLength(20)]
    public string? PhoneNumber { get; set; }

    [JsonPropertyName("bio")]
    [MaxLength(300)]
    public string? Bio { get; set; }

    /// <summary>
    /// What the member is known for, as free text. An empty string clears it.
    /// </summary>
    [JsonPropertyName("specialities")]
    [MaxLength(300)]
    public string? Specialities { get; set; }

    /// <summary>
    /// Whole years of professional experience (0–80). Omitted leaves it
    /// untouched; set <see cref="ClearYearsOfExperience"/> to remove it.
    /// </summary>
    [JsonPropertyName("yearsOfExperience")]
    [Range(0, 80)]
    public int? YearsOfExperience { get; set; }

    /// <summary>Explicitly clear the stated experience rather than leaving it untouched.</summary>
    [JsonPropertyName("clearYearsOfExperience")]
    public bool ClearYearsOfExperience { get; set; }

    [JsonPropertyName("isPublic")] public bool? IsPublic { get; set; }
    [JsonPropertyName("isBookable")] public bool? IsBookable { get; set; }

    /// <summary>Position (job title) to assign, or <c>null</c> to clear it.</summary>
    [JsonPropertyName("positionId")] public Guid? PositionId { get; set; }

    /// <summary>Explicitly clear the position rather than leaving it untouched.</summary>
    [JsonPropertyName("clearPosition")] public bool ClearPosition { get; set; }

    [JsonPropertyName("dailyGoalOverride")] public int? DailyGoalOverride { get; set; }
    [JsonPropertyName("appointmentDailyGoalOverride")] public int? AppointmentDailyGoalOverride { get; set; }
    [JsonPropertyName("clearDailyGoalOverride")] public bool ClearDailyGoalOverride { get; set; }
    [JsonPropertyName("clearAppointmentDailyGoalOverride")] public bool ClearAppointmentDailyGoalOverride { get; set; }

    /// <summary>Media row to use as the profile photo (the media module owns the bytes).</summary>
    [JsonPropertyName("avatarMediaId")] public Guid? AvatarMediaId { get; set; }
}

/// <summary>Replacement set of services a staff member delivers. The array is the whole set.</summary>
public class SetStaffServicesRequest
{
    [JsonPropertyName("serviceIds")] public List<Guid> ServiceIds { get; set; } = [];
}

/// <summary>One weekday to save as part of the recurring working week.</summary>
public class StaffAvailabilityDayInput
{
    [JsonPropertyName("dayOfWeek")] public int DayOfWeek { get; set; }
    [JsonPropertyName("isWorking")] public bool IsWorking { get; set; }
    [JsonPropertyName("startMinute")] public int StartMinute { get; set; }
    [JsonPropertyName("endMinute")] public int EndMinute { get; set; }
    [JsonPropertyName("breakStartMinute")] public int? BreakStartMinute { get; set; }
    [JsonPropertyName("breakEndMinute")] public int? BreakEndMinute { get; set; }
}

/// <summary>Replacement working week. Days not present keep their current value.</summary>
public class SetStaffAvailabilityRequest
{
    [JsonPropertyName("days")] public List<StaffAvailabilityDayInput> Days { get; set; } = [];
}

/// <summary>Dated time off for a staff member.</summary>
public class AddStaffTimeOffRequest
{
    [JsonPropertyName("from")] public DateOnly From { get; set; }
    [JsonPropertyName("to")] public DateOnly To { get; set; }
    [JsonPropertyName("reason")]
    [MaxLength(200)]
    public string? Reason { get; set; }
}

/// <summary>Result of an activate/deactivate transition.</summary>
public class StaffStatusResponse
{
    [JsonPropertyName("userId")] public Guid UserId { get; set; }
    /// <summary><c>Active</c> | <c>Suspended</c>.</summary>
    [JsonPropertyName("membershipStatus")] public string MembershipStatus { get; set; } = string.Empty;
    [JsonPropertyName("isActive")] public bool IsActive { get; set; }
    [JsonPropertyName("endedAt")] public DateTime? EndedAt { get; set; }
    [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;
}
