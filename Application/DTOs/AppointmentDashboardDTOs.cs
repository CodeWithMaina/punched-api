using System.Text.Json.Serialization;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Application.DTOs;

/// <summary>
/// Scoped pagination metadata shared by appointment dashboard endpoints.
/// </summary>
public class PaginationMeta
{
    [JsonPropertyName("page")]
    public int Page { get; set; }

    [JsonPropertyName("pageSize")]
    public int PageSize { get; set; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }

    [JsonPropertyName("totalPages")]
    public int TotalPages { get; set; }
}

/// <summary>
/// A single appointment in the owner dashboard — a lightweight projection that
/// resolves customer/staff/service names at query time so the response never
/// requires a subsequent request to look up related entities.
/// </summary>
public class AppointmentDashboardItemResponse
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("businessId")]
    public Guid BusinessId { get; set; }

    [JsonPropertyName("scheduledAt")]
    public DateTime ScheduledAt { get; set; }

    [JsonPropertyName("endAt")]
    public DateTime EndAt { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("serviceId")]
    public Guid ServiceId { get; set; }

    [JsonPropertyName("serviceName")]
    public string ServiceName { get; set; } = string.Empty;

    [JsonPropertyName("customerId")]
    public Guid CustomerId { get; set; }

    /// <summary>`guest` | `registered` — how the customer belongs to this business.</summary>
    [JsonPropertyName("customerType")]
    public string CustomerType { get; set; } = "registered";

    [JsonPropertyName("customerName")]
    public string CustomerName { get; set; } = string.Empty;

    [JsonPropertyName("customerEmail")]
    public string? CustomerEmail { get; set; }

    [JsonPropertyName("customerPhone")]
    public string? CustomerPhone { get; set; }

    [JsonPropertyName("customerAvatarUrl")]
    public string? CustomerAvatarUrl { get; set; }

    [JsonPropertyName("staffUserId")]
    public Guid? StaffUserId { get; set; }

    [JsonPropertyName("staffName")]
    public string? StaffName { get; set; }

    [JsonPropertyName("staffEmail")]
    public string? StaffEmail { get; set; }

    [JsonPropertyName("staffAvatarUrl")]
    public string? StaffAvatarUrl { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Derived payment state, aggregated from the payment ledger (never a column on
    /// the appointment): <c>paid</c>, <c>partially_paid</c>, <c>awaiting_verification</c>,
    /// <c>unpaid</c>, <c>failed</c> or <c>refunded</c>.
    /// </summary>
    [JsonPropertyName("paymentStatus")]
    public string PaymentStatus { get; set; } = "unpaid";

    /// <summary>
    /// Genuinely settled amount — provider-confirmed (<c>Success</c>) plus
    /// owner-verified (<c>Verified</c>) rows only. Awaiting claims, failed attempts,
    /// refunds and outstanding-balance rows are all excluded.
    /// </summary>
    [JsonPropertyName("amountPaid")]
    public decimal AmountPaid { get; set; }

    /// <summary>Still owed: the expected total minus what settled, never negative.</summary>
    [JsonPropertyName("balance")]
    public decimal Balance { get; set; }
}

/// <summary>
/// Lightweight customer reference data for appointment filter dropdowns.
/// Unpaginated so the filter UI can render the full list without extra calls.
/// </summary>
public class CustomerDashboardItemResponse
{
    [JsonPropertyName("userId")]
    public Guid UserId { get; set; }

    [JsonPropertyName("fullName")]
    public string FullName { get; set; } = string.Empty;

    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;

    [JsonPropertyName("phoneNumber")]
    public string? PhoneNumber { get; set; }

    [JsonPropertyName("avatarUrl")]
    public string? AvatarUrl { get; set; }

    [JsonPropertyName("enrolledAt")]
    public DateTime EnrolledAt { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "active";

    [JsonPropertyName("totalStamps")]
    public int TotalStamps { get; set; }

    [JsonPropertyName("lastStampAt")]
    public DateTime? LastStampAt { get; set; }
}

/// <summary>
/// Lightweight staff reference data for appointment filter dropdowns.
/// Unpaginated so the filter UI can render the full list without extra calls.
/// </summary>
public class StaffDashboardItemResponse
{
    [JsonPropertyName("userId")]
    public Guid UserId { get; set; }

    [JsonPropertyName("fullName")]
    public string FullName { get; set; } = string.Empty;

    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;

    [JsonPropertyName("avatarUrl")]
    public string? AvatarUrl { get; set; }

    [JsonPropertyName("phoneNumber")]
    public string? PhoneNumber { get; set; }
}

/// <summary>
/// Lightweight service reference data for appointment filter dropdowns.
/// Unpaginated so the filter UI can render the full list without extra calls.
/// </summary>
public class ServiceDashboardItemResponse
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("durationMinutes")]
    public int DurationMinutes { get; set; }

    [JsonPropertyName("price")]
    public decimal Price { get; set; }

    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; }

    [JsonPropertyName("showcase")]
    public bool Showcase { get; set; }
}

/// <summary>
/// Single consolidated appointment dashboard response:
/// paginated appointments + lightweight reference data for filter dropdowns.
/// </summary>
public class AppointmentDashboardResponse
{
    [JsonPropertyName("meta")]
    public PaginationMeta Meta { get; set; } = new();

    [JsonPropertyName("appointments")]
    public List<AppointmentDashboardItemResponse> Appointments { get; set; } = new();

    // ── Staff and services only ─────────────────────────────
    // There is deliberately NO customer bucket. It used to ship every active customer of
    // the business with every page of appointments, which is the one thing that grows
    // without bound: a salon with 5,000 customers paid for all 5,000 rows on every keystroke
    // of a phone number. Both consumers that used it (the booking and rebook drawers) now
    // search customers server-side through the paginated search route, which is the same
    // picker the booking wizard uses.

    [JsonPropertyName("staff")]
    public List<StaffDashboardItemResponse> Staff { get; set; } = new();

    /// <summary>
    /// Whether the optional Staff module is entitled for this business. When
    /// <c>false</c> the caller must not offer staff selection or staff filtering,
    /// and should not request a staff list at all. Historical appointments still
    /// carry their assigned staff in <see cref="Appointments"/>.
    /// </summary>
    [JsonPropertyName("staffModuleEnabled")]
    public bool StaffModuleEnabled { get; set; } = true;

    [JsonPropertyName("services")]
    public List<ServiceDashboardItemResponse> Services { get; set; } = new();
}
