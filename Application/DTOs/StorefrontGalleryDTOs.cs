using System.Text.Json.Serialization;

namespace PunchedApi.Application.DTOs;

// ═══════════════════════════════════════════════════════════════
//  PUBLIC STOREFRONT GALLERY
//
//  A business's published photo gallery, exposed to anonymous storefront
//  visitors. The owner already uploads and orders gallery media through
//  `IMediaService` (business_media + media); this is the read model that
//  lets a visitor actually see it without one request per image and
//  without the browser ever learning a storage key.
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// One published image in a business's public gallery.
///
/// <para>Only the resolved, processed variants are exposed — never the
/// object-store source key or the storage provider. <see cref="Variants"/>
/// is empty while an image is still processing, which is the signal the
/// storefront uses to skip it rather than render a broken tile.</para>
/// </summary>
public sealed class PublicGalleryImageResponse
{
    /// <summary>The media row id (stable across variant regeneration).</summary>
    [JsonPropertyName("mediaId")] public Guid MediaId { get; set; }

    /// <summary>Owner-written caption, or null when none was written.</summary>
    [JsonPropertyName("caption")] public string? Caption { get; set; }

    /// <summary>Owner-controlled display order (ascending).</summary>
    [JsonPropertyName("sortOrder")] public int SortOrder { get; set; }

    /// <summary>Owner-marked featured image (used for a hero/spotlight tile).</summary>
    [JsonPropertyName("featured")] public bool Featured { get; set; }

    /// <summary>Intrinsic width of the processed source, when known.</summary>
    [JsonPropertyName("width")] public int? Width { get; set; }

    /// <summary>Intrinsic height of the processed source, when known.</summary>
    [JsonPropertyName("height")] public int? Height { get; set; }

    /// <summary>Ready variants (webp/jpeg at the configured sizes).</summary>
    [JsonPropertyName("variants")] public IReadOnlyList<MediaVariantResponse> Variants { get; set; } = [];
}

/// <summary>
/// `GET /v1/businesses/{businessId}/gallery` — the business's published
/// gallery, ordered for display. Empty (not an error) when the business
/// has no public gallery images.
/// </summary>
public sealed class PublicGalleryResponse
{
    [JsonPropertyName("businessId")] public Guid BusinessId { get; set; }

    [JsonPropertyName("images")] public IReadOnlyList<PublicGalleryImageResponse> Images { get; set; } = [];
}
