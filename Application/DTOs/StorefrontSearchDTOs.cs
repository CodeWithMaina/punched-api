using System.Text.Json.Serialization;

namespace PunchedApi.Application.DTOs;

// ═══════════════════════════════════════════════════════════════
//  PUBLIC STOREFRONT SEARCH
//
//  Business-scoped, server-side search across everything a business
//  publishes: its showcased services and its publicly visible staff.
//  Replaces the previous browser-side filtering of two full collections.
// ═══════════════════════════════════════════════════════════════

/// <summary>The entity kinds a storefront search can return.</summary>
public static class StorefrontSearchTypes
{
    public const string All = "all";
    public const string Services = "services";
    public const string Staff = "team";

    public static readonly IReadOnlySet<string> All_ = new HashSet<string>(StringComparer.Ordinal)
    {
        All, Services, Staff
    };

    /// <summary>Normalises a caller-supplied scope, defaulting to `all`.</summary>
    public static string Normalise(string? value)
    {
        var candidate = value?.Trim().ToLowerInvariant();
        return candidate is not null && All_.Contains(candidate) ? candidate : All;
    }
}

/// <summary>One search hit. `type` is `service` or `staff`.</summary>
public sealed class StorefrontSearchResultResponse
{
    /// <summary>`service` or `staff`.</summary>
    [JsonPropertyName("type")] public string Type { get; set; } = string.Empty;

    /// <summary>The service id or the staff member's user id.</summary>
    [JsonPropertyName("id")] public Guid Id { get; set; }

    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;

    /// <summary>Category name (service) or job title (staff), when known.</summary>
    [JsonPropertyName("subtitle")] public string? Subtitle { get; set; }

    /// <summary>Service description or staff biography, when present.</summary>
    [JsonPropertyName("description")] public string? Description { get; set; }

    [JsonPropertyName("categoryName")] public string? CategoryName { get; set; }

    [JsonPropertyName("price")] public decimal? Price { get; set; }

    [JsonPropertyName("durationMinutes")] public int? DurationMinutes { get; set; }

    /// <summary>Service cover variants (empty for staff results).</summary>
    [JsonPropertyName("imageVariants")] public IReadOnlyList<MediaVariantResponse> ImageVariants { get; set; } = [];

    /// <summary>Staff avatar URL (null for service results).</summary>
    [JsonPropertyName("avatarUrl")] public string? AvatarUrl { get; set; }

    /// <summary>The public service ids a staff member performs (empty for services).</summary>
    [JsonPropertyName("serviceIds")] public IReadOnlyList<Guid> ServiceIds { get; set; } = [];
}

/// <summary>
/// `GET /v1/businesses/{businessId}/search` — a page of matching published
/// services and publicly visible staff, plus the categories the business
/// publishes.
/// </summary>
public sealed class StorefrontSearchResponse
{
    [JsonPropertyName("query")] public string Query { get; set; } = string.Empty;

    /// <summary>The normalised scope the server applied (`all` | `services` | `team`).</summary>
    [JsonPropertyName("type")] public string Type { get; set; } = StorefrontSearchTypes.All;

    [JsonPropertyName("items")] public IReadOnlyList<StorefrontSearchResultResponse> Items { get; set; } = [];

    [JsonPropertyName("totalCount")] public int TotalCount { get; set; }

    [JsonPropertyName("page")] public int Page { get; set; }

    [JsonPropertyName("pageSize")] public int PageSize { get; set; }

    [JsonPropertyName("totalPages")] public int TotalPages { get; set; }

    /// <summary>The business's active, published category names (for scope chips).</summary>
    [JsonPropertyName("categories")] public IReadOnlyList<string> Categories { get; set; } = [];
}
