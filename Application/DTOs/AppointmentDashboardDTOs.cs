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

    [JsonPropertyName("customerName")]
    public string CustomerName { get; set; } = string.Empty;

    [JsonPropertyName("customerEmail")]
    public string? CustomerEmail { get; set; }

    [JsonPropertyName("customerPhone")]
    public string? CustomerPhone { get; set; }

    [JsonPropertyName("staffUserId")]
    public Guid? StaffUserId { get; set; }

    [JsonPropertyName("staffName")]
    public string? StaffName { get; set; }

    [JsonPropertyName("staffEmail")]
    public string? StaffEmail { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; }
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

    [JsonPropertyName("customers")]
    public List<CustomerDashboardItemResponse> Customers { get; set; } = new();

    [JsonPropertyName("staff")]
    public List<StaffDashboardItemResponse> Staff { get; set; } = new();

    [JsonPropertyName("services")]
    public List<ServiceDashboardItemResponse> Services { get; set; } = new();
}
