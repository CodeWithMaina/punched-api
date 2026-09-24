using System.Text.Json.Serialization;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Application.DTOs;

// ═══════════════════════════════════════════════════════════════
//  CARD ASSET DTOs
//
//  Uploaded branding assets (logo, background, stamp icons, artwork) used
//  purely for loyalty card *presentation*. No loyalty state is expressed here.
//
//  Note what is deliberately absent from the request: business id, owner id,
//  asset id, content type, extension and dimensions. The server derives the
//  first two from the authenticated principal and the rest from the detected
//  file signature, so none of them can be forged.
// ═══════════════════════════════════════════════════════════════

/// <summary>POST /v1/card-assets request (multipart) — purpose only.</summary>
public class UploadCardAssetRequest
{
    /// <summary>One of <see cref="CardAssetPurposes"/>. Defaults to ARTWORK.</summary>
    [JsonPropertyName("purpose")]
    public string? Purpose { get; set; }
}

/// <summary>Asset projection returned to the owning business.</summary>
public class CardAssetResponse
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("businessId")]
    public Guid BusinessId { get; set; }

    /// <summary>Purpose key the asset was uploaded for.</summary>
    [JsonPropertyName("purpose")]
    public string Purpose { get; set; } = CardAssetPurposes.Artwork;

    /// <summary>Detected content type (e.g. <c>image/png</c>) — never client-declared.</summary>
    [JsonPropertyName("contentType")]
    public string ContentType { get; set; } = string.Empty;

    /// <summary>Canonical extension derived from the detected signature.</summary>
    [JsonPropertyName("fileExtension")]
    public string FileExtension { get; set; } = string.Empty;

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; }

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    /// <summary>Lower-case hex SHA-256 of the stored payload.</summary>
    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    /// <summary><c>active</c> | <c>deleted</c>.</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "active";

    /// <summary>Sanitized display-only original filename (never a storage path).</summary>
    [JsonPropertyName("originalFileName")]
    public string? OriginalFileName { get; set; }

    /// <summary>Authenticated, tenant-scoped content URL for this asset.</summary>
    [JsonPropertyName("contentUrl")]
    public string ContentUrl { get; set; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Result payload for a content download.</summary>
public sealed class CardAssetContentResult : IAsyncDisposable
{
    /// <summary>Open payload stream. Ownership transfers to the caller.</summary>
    public required Stream Content { get; init; }

    /// <summary>Content type from the stored, signature-detected value.</summary>
    public required string ContentType { get; init; }

    /// <summary>Byte length when known.</summary>
    public long Length { get; init; }

    /// <summary>Absolute-or-relative file name hint (generated, never user input).</summary>
    public required string FileName { get; init; }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}
