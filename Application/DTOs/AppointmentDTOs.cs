using System.Text.Json.Serialization;

namespace PunchedApi.Application.DTOs;

// ═══════════════════════════════════════════════════════════════
//  BOOKING — REQUESTS
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// Queries staff availability within a date range for one or more services.
/// </summary>
public class AvailabilityQueryRequest
{
    [JsonPropertyName("businessId")]
    public Guid BusinessId { get; set; }

    [JsonPropertyName("serviceIds")]
    public Guid[] ServiceIds { get; set; } = [];

    [JsonPropertyName("staffUserId")]
    public Guid? StaffUserId { get; set; }

    [JsonPropertyName("startDate")]
    public DateOnly StartDate { get; set; }

    [JsonPropertyName("endDate")]
    public DateOnly EndDate { get; set; }
}

/// <summary>
/// Customer self-service booking. The caller's customerId is always forced server-side.
/// </summary>
public class CreateAppointmentRequest
{
    [JsonPropertyName("businessId")]
    public Guid BusinessId { get; set; }

    [JsonPropertyName("serviceIds")]
    public Guid[] ServiceIds { get; set; } = [];

    [JsonPropertyName("staffUserId")]
    public Guid? StaffUserId { get; set; }

    [JsonPropertyName("scheduledAt")]
    public DateTime ScheduledAt { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }
}

/// <summary>
/// Business/Staff booking on behalf of a customer. customerId is required.
/// </summary>
public class CreateAppointmentOnBehalfRequest
{
    [JsonPropertyName("businessId")]
    public Guid BusinessId { get; set; }

    [JsonPropertyName("serviceIds")]
    public Guid[] ServiceIds { get; set; } = [];

    [JsonPropertyName("staffUserId")]
    public Guid? StaffUserId { get; set; }

    [JsonPropertyName("scheduledAt")]
    public DateTime ScheduledAt { get; set; }

    [JsonPropertyName("customerId")]
    public Guid CustomerId { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }
}

/// <summary>
/// Reschedules an existing appointment. serviceIds/staffUserId are optional; when
/// provided they replace the previously booked configuration.
/// </summary>
public class RescheduleAppointmentRequest
{
    [JsonPropertyName("scheduledAt")]
    public DateTime ScheduledAt { get; set; }

    [JsonPropertyName("serviceIds")]
    public Guid[]? ServiceIds { get; set; }

    [JsonPropertyName("staffUserId")]
    public Guid? StaffUserId { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }
}

/// <summary>
/// Cancels an existing appointment.
/// </summary>
public class CancelAppointmentRequest
{
    [JsonPropertyName("note")]
    public string? Note { get; set; }
}

// ═══════════════════════════════════════════════════════════════
//  BOOKING — AVAILABILITY
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// Why a slot cannot be booked. Rendered verbatim by the booking UI, so the
/// values are part of the API contract.
/// </summary>
public static class SlotStatuses
{
    public const string Available = "available";
    public const string Booked = "booked";
    public const string OffDuty = "off_duty";
    public const string Past = "past";
}

/// <summary>
/// Minimal staff descriptor embedded in availability payloads.
/// </summary>
public class AvailabilityStaffRef
{
    [JsonPropertyName("staffUserId")]
    public Guid StaffUserId { get; set; }

    [JsonPropertyName("staffName")]
    public string StaffName { get; set; } = string.Empty;

    [JsonPropertyName("avatarUrl")]
    public string? AvatarUrl { get; set; }
}

/// <summary>
/// An alternative staff member the customer can switch to when their preferred
/// staff member (or the whole roster) is fully booked on the viewed day.
/// </summary>
public class StaffAvailabilitySuggestion : AvailabilityStaffRef
{
    [JsonPropertyName("availableSlotCount")]
    public int AvailableSlotCount { get; set; }

    [JsonPropertyName("nextAvailableUtc")]
    public DateTime? NextAvailableUtc { get; set; }

    /// <summary>Business-local "HH:mm" of <see cref="NextAvailableUtc"/>.</summary>
    [JsonPropertyName("nextAvailableLocalTime")]
    public string? NextAvailableLocalTime { get; set; }
}

/// <summary>
/// A single slot on the bookable grid. Unlike the legacy engine this is emitted
/// for unbookable times too, so the UI can render the full day and mark the
/// blocked times instead of showing an empty state.
/// </summary>
public class AvailabilitySlotResponse
{
    [JsonPropertyName("startAtUtc")]
    public DateTime StartAtUtc { get; set; }

    [JsonPropertyName("endAtUtc")]
    public DateTime EndAtUtc { get; set; }

    /// <summary>Staff who would take the slot. Null when nobody is free at this time.</summary>
    [JsonPropertyName("staffUserId")]
    public Guid? StaffUserId { get; set; }

    [JsonPropertyName("staffName")]
    public string StaffName { get; set; } = string.Empty;

    [JsonPropertyName("serviceIds")]
    public Guid[] ServiceIds { get; set; } = [];

    /// <summary>One of <see cref="SlotStatuses"/>.</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = SlotStatuses.Available;

    [JsonPropertyName("isAvailable")]
    public bool IsAvailable { get; set; }

    /// <summary>Business-local date, "yyyy-MM-dd".</summary>
    [JsonPropertyName("localDate")]
    public string LocalDate { get; set; } = string.Empty;

    /// <summary>Business-local start time, "HH:mm".</summary>
    [JsonPropertyName("localTime")]
    public string LocalTime { get; set; } = string.Empty;

    /// <summary>"morning" | "afternoon" | "evening" (business-local).</summary>
    [JsonPropertyName("dayPart")]
    public string DayPart { get; set; } = string.Empty;

    /// <summary>
    /// Other staff free at this exact time. Populated when the requested staff
    /// member is blocked, so the UI can offer an instant swap.
    /// </summary>
    [JsonPropertyName("alternativeStaff")]
    public List<AvailabilityStaffRef> AlternativeStaff { get; set; } = [];
}

/// <summary>
/// One business-local day of the bookable grid plus its rollups.
/// </summary>
public class DayAvailabilityResponse
{
    [JsonPropertyName("date")]
    public string Date { get; set; } = string.Empty;

    /// <summary>Short weekday label in the business locale, e.g. "Mon".</summary>
    [JsonPropertyName("weekday")]
    public string Weekday { get; set; } = string.Empty;

    [JsonPropertyName("dayOfMonth")]
    public int DayOfMonth { get; set; }

    /// <summary>False when nobody in scope works this day (closed / day off).</summary>
    [JsonPropertyName("isOpen")]
    public bool IsOpen { get; set; }

    [JsonPropertyName("totalSlots")]
    public int TotalSlots { get; set; }

    [JsonPropertyName("availableSlots")]
    public int AvailableSlots { get; set; }

    [JsonPropertyName("firstAvailableUtc")]
    public DateTime? FirstAvailableUtc { get; set; }

    [JsonPropertyName("slots")]
    public List<AvailabilitySlotResponse> Slots { get; set; } = [];

    /// <summary>Staff with openings this day, excluding the currently selected one.</summary>
    [JsonPropertyName("suggestions")]
    public List<StaffAvailabilitySuggestion> Suggestions { get; set; } = [];
}

/// <summary>
/// Full availability calendar for the booking wizard's time step.
/// </summary>
public class AvailabilityCalendarResponse
{
    [JsonPropertyName("businessId")]
    public Guid BusinessId { get; set; }

    [JsonPropertyName("businessName")]
    public string BusinessName { get; set; } = string.Empty;

    [JsonPropertyName("timeZoneId")]
    public string TimeZoneId { get; set; } = string.Empty;

    [JsonPropertyName("serviceIds")]
    public Guid[] ServiceIds { get; set; } = [];

    [JsonPropertyName("totalDurationMinutes")]
    public int TotalDurationMinutes { get; set; }

    [JsonPropertyName("slotIntervalMinutes")]
    public int SlotIntervalMinutes { get; set; }

    [JsonPropertyName("leadTimeMinutes")]
    public int LeadTimeMinutes { get; set; }

    /// <summary>Echo of the requested staff filter (null = "any available").</summary>
    [JsonPropertyName("staffUserId")]
    public Guid? StaffUserId { get; set; }

    [JsonPropertyName("staffName")]
    public string? StaffName { get; set; }

    [JsonPropertyName("days")]
    public List<DayAvailabilityResponse> Days { get; set; } = [];

    /// <summary>Machine-readable reason the calendar is empty, when it is.</summary>
    [JsonPropertyName("noticeCode")]
    public string? NoticeCode { get; set; }

    [JsonPropertyName("notice")]
    public string? Notice { get; set; }
}

// ═══════════════════════════════════════════════════════════════
//  BOOKING — RESPONSES
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// Full appointment view. updatedAt is derived from the most recent
/// AppointmentStatusHistory.ChangedAt (fallback: CreatedAt).
/// </summary>
/// <summary>
/// DB-first filter + pagination query for the customer's own appointment list.
/// Every dimension is applied at the database layer; only the requested page is
/// returned (never load-then-filter in JS).
/// </summary>
public class CustomerAppointmentsQueryRequest
{
    [JsonPropertyName("businessId")]
    public Guid? BusinessId { get; set; }

    [JsonPropertyName("staffUserId")]
    public Guid? StaffUserId { get; set; }

    [JsonPropertyName("serviceId")]
    public Guid? ServiceId { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    /// <summary>Inclusive date/time range start (database-side filter).</summary>
    [JsonPropertyName("from")]
    public DateTime? From { get; set; }

    /// <summary>Inclusive date/time range end (database-side filter).</summary>
    [JsonPropertyName("to")]
    public DateTime? To { get; set; }

    /// <summary>Sort strategy: "newest" (default), "oldest" or "upcoming".</summary>
    [JsonPropertyName("sortBy")]
    public string? SortBy { get; set; }

    [JsonPropertyName("page")]
    public int Page { get; set; } = 1;

    [JsonPropertyName("pageSize")]
    public int PageSize { get; set; } = 20;
}

/// <summary>A single distinct value surfaced for the appointment filter UI (DB projection).</summary>
public class AppointmentFilterOption
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// Distinct facets (businesses, staff, services) drawn from the customer's own
/// appointments — a cheap, DB-driven source of filter options (never a full list load).
/// </summary>
public class CustomerAppointmentFiltersResponse
{
    [JsonPropertyName("businesses")]
    public List<AppointmentFilterOption> Businesses { get; set; } = [];

    [JsonPropertyName("staff")]
    public List<AppointmentFilterOption> Staff { get; set; } = [];

    [JsonPropertyName("services")]
    public List<AppointmentFilterOption> Services { get; set; } = [];
}
public class AppointmentResponse
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("businessId")]
    public Guid BusinessId { get; set; }

    [JsonPropertyName("customerId")]
    public Guid CustomerId { get; set; }

    [JsonPropertyName("staffUserId")]
    public Guid? StaffUserId { get; set; }

    [JsonPropertyName("scheduledAt")]
    public DateTime ScheduledAt { get; set; }

    [JsonPropertyName("endAt")]
    public DateTime EndAt { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("services")]
    public List<AppointmentServiceSnapshot> Services { get; set; } = new();

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Populated while a customer reschedule request awaits business confirmation.
    /// </summary>
    [JsonPropertyName("pendingReschedule")]
    public AppointmentRescheduleRequestResponse? PendingReschedule { get; set; }
}

/// <summary>
/// A pending customer reschedule proposal attached to an appointment response.
/// </summary>
public class AppointmentRescheduleRequestResponse
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("proposedScheduledAt")]
    public DateTime ProposedScheduledAt { get; set; }

    [JsonPropertyName("proposedEndAt")]
    public DateTime ProposedEndAt { get; set; }

    [JsonPropertyName("proposedStaffUserId")]
    public Guid? ProposedStaffUserId { get; set; }

    [JsonPropertyName("proposedServices")]
    public List<AppointmentServiceSnapshot> ProposedServices { get; set; } = new();

    [JsonPropertyName("requestedAt")]
    public DateTime RequestedAt { get; set; }
}

/// <summary>
/// Immutable snapshot of a service at booking time.
/// </summary>
public class AppointmentServiceSnapshot
{
    [JsonPropertyName("serviceCatalogItemId")]
    public Guid ServiceCatalogItemId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("durationMinutes")]
    public int DurationMinutes { get; set; }

    [JsonPropertyName("price")]
    public decimal Price { get; set; }

    [JsonPropertyName("sortOrder")]
    public int SortOrder { get; set; }
}

/// <summary>
/// Lightweight calendar item used by calendar views.
/// </summary>
public class AppointmentCalendarItemResponse
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("staffUserId")]
    public Guid? StaffUserId { get; set; }

    [JsonPropertyName("customerId")]
    public Guid CustomerId { get; set; }

    [JsonPropertyName("startAt")]
    public DateTime StartAt { get; set; }

    [JsonPropertyName("endAt")]
    public DateTime EndAt { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("services")]
    public List<AppointmentServiceSnapshot> Services { get; set; } = new();
}
