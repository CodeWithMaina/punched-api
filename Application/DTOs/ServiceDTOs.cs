using System.Text.Json.Serialization;
using PunchedApi.Domain.Entities;

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

    [JsonPropertyName("businessName")]
    public string? BusinessName { get; set; }

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

    /// <summary>
    /// The business-scoped category this service is filed under, or null when
    /// the service is uncategorised. Uncategorised services are a supported
    /// state, not an error.
    /// </summary>
    [JsonPropertyName("categoryId")]
    public Guid? CategoryId { get; set; }

    /// <summary>
    /// The category's display name, resolved server-side so a client never has
    /// to join two collections to render a group heading.
    /// </summary>
    [JsonPropertyName("categoryName")]
    public string? CategoryName { get; set; }

    /// <summary>Category display order, so a client can sort groups without a second call.</summary>
    [JsonPropertyName("categorySortOrder")]
    public int? CategorySortOrder { get; set; }

    [JsonPropertyName("imageMediaId")]
    public Guid? ImageMediaId { get; set; }

    [JsonPropertyName("imageStatus")]
    public string? ImageStatus { get; set; }

    [JsonPropertyName("imageVariants")]
    public IReadOnlyList<ServiceImageVariantResponse> ImageVariants { get; set; } = [];

    /// <summary>
    /// Every image attached to the service, ordered by display order — index 0
    /// is the cover. The legacy <see cref="ImageMediaId"/>/<see cref="ImageStatus"/>/
    /// <see cref="ImageVariants"/> fields keep describing that cover image so an
    /// older client (the storefront) keeps working unchanged.
    /// </summary>
    [JsonPropertyName("images")]
    public IReadOnlyList<ServiceImageResponse> Images { get; set; } = [];

    /// <summary>
    /// The owner-facing state: <c>active</c>, <c>paused</c> or <c>archived</c>.
    /// Derived server-side from <c>IsActive</c> + <c>IsArchived</c> so a client
    /// never has to combine two booleans to render a status pill.
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = ServiceCatalogStatuses.Active;

    /// <summary>True when the business has filed the service away.</summary>
    [JsonPropertyName("isArchived")]
    public bool IsArchived { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// One image attached to a service. <see cref="DisplayOrder"/> is the gallery
/// order and <see cref="IsCover"/> marks the single image at order 0.
/// </summary>
public sealed class ServiceImageResponse
{
    /// <summary>The media row's id — what an image write sends back.</summary>
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("mediaId")]
    public Guid MediaId { get; set; }

    /// <summary>Cover-sized URL (the largest ready variant), or null while processing.</summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("width")]
    public int? Width { get; set; }

    [JsonPropertyName("height")]
    public int? Height { get; set; }

    [JsonPropertyName("displayOrder")]
    public int DisplayOrder { get; set; }

    /// <summary>True for the first image only: the service's cover.</summary>
    [JsonPropertyName("isCover")]
    public bool IsCover { get; set; }

    [JsonPropertyName("variants")]
    public IReadOnlyList<ServiceImageVariantResponse> Variants { get; set; } = [];
}

/// <summary>
/// Replaces the ordered image set of one service. The array order IS the display
/// order, so index 0 becomes the cover; an empty array removes every image.
/// </summary>
public sealed class SetServiceImagesRequest
{
    [JsonPropertyName("mediaIds")]
    public List<Guid> MediaIds { get; set; } = [];
}

public sealed class ServiceImageVariantResponse
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    [JsonPropertyName("format")]
    public string Format { get; set; } = string.Empty;
}

/// <summary>
/// A staff member eligible to perform a set of services (public booking view).
/// Only identity plus the publicly authorized contact number — never internal
/// metrics (appointments/stamps/goals/activity), which stay owner-scoped.
/// </summary>
public class EligibleStaffResponse
{
    [JsonPropertyName("userId")]
    public Guid UserId { get; set; }

    [JsonPropertyName("fullName")]
    public string FullName { get; set; } = string.Empty;

    [JsonPropertyName("avatarUrl")]
    public string? AvatarUrl { get; set; }

    /// <summary>
    /// Publicly authorized contact number so customers can call if they wish.
    /// Null when the business/staff member has not shared one.
    /// </summary>
    [JsonPropertyName("phoneNumber")]
    public string? PhoneNumber { get; set; }

    /// <summary>
    /// The business's publicly visible services this member is assigned to.
    ///
    /// Sent so the storefront can show "who does what" — and offer a specialist
    /// for a chosen service — from the payload it already loads, instead of one
    /// eligibility request per service. Only active, showcased services of this
    /// business can appear here.
    /// </summary>
    [JsonPropertyName("serviceIds")]
    public List<Guid> ServiceIds { get; set; } = new();

    /// <summary>
    /// The member's current business-assigned job title, or null when the
    /// business has not set one.
    ///
    /// Purely descriptive: a position grants no permission and no service
    /// eligibility (see <see cref="PunchedApi.Domain.Entities.StaffPosition"/>).
    /// Only the label is exposed — never the position's id, ordering or
    /// description.
    /// </summary>
    [JsonPropertyName("positionName")]
    public string? PositionName { get; set; }

    /// <summary>
    /// The member's short storefront biography, when the business has written
    /// one. This projection only ever contains members whose membership is
    /// public, so anything here is already approved for publication.
    /// </summary>
    [JsonPropertyName("bio")]
    public string? Bio { get; set; }

    /// <summary>
    /// What the member is known for, as free text ("Balayage, Curly cuts").
    /// Null when the business has not stated any — the storefront then shows
    /// nothing rather than a placeholder.
    /// </summary>
    [JsonPropertyName("specialities")]
    public string? Specialities { get; set; }

    /// <summary>
    /// Whole years of professional experience, or null when not stated.
    /// </summary>
    [JsonPropertyName("yearsOfExperience")]
    public int? YearsOfExperience { get; set; }
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

    /// <summary>
    /// Optional category to file the service under. Omitted leaves it
    /// uncategorised, so an older client keeps working unchanged.
    /// </summary>
    [JsonPropertyName("categoryId")]
    public Guid? CategoryId { get; set; }
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
    /// Partial update of the archive flag. Archiving also deactivates (a filed
    /// service is never bookable); clearing it restores the service to active,
    /// which is the same transition as <c>POST me/{id}/restore</c>.
    /// </summary>
    [JsonPropertyName("isArchived")]
    public bool? IsArchived { get; set; }

    /// <summary>
    /// Partial update of the public-showcase flag. Null leaves it unchanged,
    /// so the owner toggle works without a full-object write.
    /// </summary>
    [JsonPropertyName("showcase")]
    public bool? Showcase { get; set; }

    /// <summary>
    /// Moves the service to a category. Null leaves the current category
    /// unchanged; send <see cref="ClearCategory"/> = true to uncategorise.
    /// </summary>
    [JsonPropertyName("categoryId")]
    public Guid? CategoryId { get; set; }

    /// <summary>
    /// Explicitly removes the service from its category. Separate from
    /// <see cref="CategoryId"/> so "leave as is" and "uncategorise" are
    /// distinguishable in a partial update.
    /// </summary>
    [JsonPropertyName("clearCategory")]
    public bool? ClearCategory { get; set; }
}
