using System.Text.Json.Serialization;

namespace PunchedApi.Application.DTOs;

// ═══════════════════════════════════════════════════════════════
//  SERVICE CATALOG — DTOs
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// A service offered by a business (public/owner views).
/// </summary>
public class ServiceCatalogItemResponse
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("businessId")]
    public Guid BusinessId { get; set; }

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

    /// <summary>
    /// Whether the service is surfaced on public surfaces. Owned by the
    /// business — clients may set it, but the PUBLIC endpoint still decides
    /// what it returns (see ServiceCatalogService.GetServicesForBusinessAsync).
    /// </summary>
    [JsonPropertyName("showcase")]
    public bool Showcase { get; set; } = true;

    [JsonPropertyName("imageMediaId")]
    public Guid? ImageMediaId { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// A staff member eligible to perform a set of services (public booking view).
/// </summary>
public class EligibleStaffResponse
{
    [JsonPropertyName("userId")]
    public Guid UserId { get; set; }

    [JsonPropertyName("fullName")]
    public string FullName { get; set; } = string.Empty;

    [JsonPropertyName("avatarUrl")]
    public string? AvatarUrl { get; set; }
}

/// <summary>
/// Creates a new catalog service.
/// </summary>
public class CreateServiceRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("durationMinutes")]
    public int DurationMinutes { get; set; }

    [JsonPropertyName("price")]
    public decimal Price { get; set; }

    /// <summary>
    /// Optional public-showcase flag. Omitted means true (published), so an
    /// older client that never sends it creates a visible service.
    /// </summary>
    [JsonPropertyName("showcase")]
    public bool? Showcase { get; set; }
}

/// <summary>
/// Partially updates a catalog service. Only provided fields are applied.
/// </summary>
public class UpdateServiceRequest
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("durationMinutes")]
    public int? DurationMinutes { get; set; }

    [JsonPropertyName("price")]
    public decimal? Price { get; set; }

    [JsonPropertyName("isActive")]
    public bool? IsActive { get; set; }

    /// <summary>
    /// Partial update of the public-showcase flag. Null leaves it unchanged,
    /// so the owner toggle works without a full-object write.
    /// </summary>
    [JsonPropertyName("showcase")]
    public bool? Showcase { get; set; }
}
