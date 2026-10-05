using System.ComponentModel.DataAnnotations;

namespace PunchedApi.Domain.Entities;

/// <summary>
/// A binary branding asset (logo, background, stamp icon, artwork) uploaded by a
/// business for use in loyalty card *presentation*.
///
/// Assets are presentation only: nothing here participates in loyalty state
/// (stamp counts, requirements, rewards, redemption or history) and no
/// presentation change may ever mutate it.
///
/// Security model:
/// <list type="bullet">
/// <item>Bytes are validated by file signature (magic number), never by the file
/// extension or the client-supplied content type.</item>
/// <item>The stored key is generated server-side
/// (<c>{businessId}/{assetId}.{ext}</c>); the original filename is kept for
/// display only and is never used as a path.</item>
/// <item>Every read/write is scoped by <see cref="BusinessId"/>, which is always
/// derived from the authenticated principal — never from the request body.</item>
/// <item>Rows are soft-deleted (<see cref="CardAssetStatus.Deleted"/>) so designs
/// that reference them keep resolving to a stable, auditable record.</item>
/// </list>
/// </summary>
public class CardAsset : BaseEntity
{
    /// <summary>FK to the owning business. Resolved from the authenticated principal, never from input.</summary>
    [Required]
    public Guid BusinessId { get; set; }

    /// <summary>FK to the uploading user (nullable for system/legacy rows).</summary>
    public Guid? UploadedByUserId { get; set; }

    /// <summary>What the asset is for — one of <see cref="CardAssetPurposes"/>.</summary>
    [Required]
    [MaxLength(40)]
    public string Purpose { get; set; } = CardAssetPurposes.Artwork;

    /// <summary>Asset category (only images are supported today).</summary>
    [Required]
    public CardAssetKind Kind { get; set; } = CardAssetKind.Image;

    /// <summary>Content type derived from the *detected* signature, e.g. <c>image/png</c>.</summary>
    [Required]
    [MaxLength(60)]
    public string ContentType { get; set; } = string.Empty;

    /// <summary>Canonical extension derived from the detected signature (never the uploaded extension).</summary>
    [Required]
    [MaxLength(10)]
    public string FileExtension { get; set; } = string.Empty;

    /// <summary>Exact byte length of the stored payload.</summary>
    [Range(1, int.MaxValue)]
    public long SizeBytes { get; set; }

    /// <summary>Decoded pixel width (from the image header).</summary>
    [Range(1, 20000)]
    public int Width { get; set; }

    /// <summary>Decoded pixel height (from the image header).</summary>
    [Range(1, 20000)]
    public int Height { get; set; }

    /// <summary>Server-generated, traversal-proof storage key. Unique across all assets.</summary>
    [Required]
    [MaxLength(300)]
    public string StorageKey { get; set; } = string.Empty;

    /// <summary>Lower-case hex SHA-256 of the stored payload (integrity + dedupe diagnostics).</summary>
    [Required]
    [MaxLength(64)]
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>Lifecycle status; only Active assets may be referenced by a design.</summary>
    [Required]
    public CardAssetStatus Status { get; set; } = CardAssetStatus.Active;

    /// <summary>
    /// Display-only, sanitized original filename. Never used to build a storage
    /// path or a URL — it exists so a business can recognise its own uploads.
    /// </summary>
    [MaxLength(255)]
    public string? OriginalFileName { get; set; }

    /// <summary>Last mutation timestamp.</summary>
    [Required]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When the asset was soft-deleted (null while Active).</summary>
    public DateTime? DeletedAt { get; set; }

    // ── Navigation ──────────────────────────────────────────
    /// <summary>The business that owns this asset.</summary>
    public virtual Business Business { get; set; } = null!;

    /// <summary>The user who uploaded the asset.</summary>
    public virtual User? UploadedByUser { get; set; }
}
/// <summary>Asset category. Images only for now — the enum leaves room for fonts later.</summary>
public enum CardAssetKind
{
    Image = 0
}

/// <summary>
/// Lifecycle of a <see cref="CardAsset"/>. Assets are never hard-deleted while a
/// design version may reference them; they move to <see cref="Deleted"/> instead.
/// </summary>
public enum CardAssetStatus
{
    /// <summary>Uploaded, validated and usable by designs.</summary>
    Active = 0,

    /// <summary>Soft-deleted by the owner. No longer selectable; historical references still resolve.</summary>
    Deleted = 1
}

/// <summary>
/// Stable purpose keys for <see cref="CardAsset.Purpose"/>. Kept as strings so a
/// designer can introduce new slots without a schema change, exactly like
/// <see cref="StampTransactions"/>.
/// </summary>
public static class CardAssetPurposes
{
    /// <summary>Business logo.</summary>
    public const string Logo = "LOGO";

    /// <summary>Card background image.</summary>
    public const string Background = "BACKGROUND";

    /// <summary>Empty stamp slot icon.</summary>
    public const string StampEmpty = "STAMP_EMPTY";

    /// <summary>Completed stamp slot icon.</summary>
    public const string StampCompleted = "STAMP_COMPLETED";

    /// <summary>Reward section illustration.</summary>
    public const string Reward = "REWARD";

    /// <summary>Free-form decorative artwork.</summary>
    public const string Artwork = "ARTWORK";

    /// <summary>All supported purpose keys.</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        Logo, Background, StampEmpty, StampCompleted, Reward, Artwork
    };

    /// <summary>True when <paramref name="purpose"/> is a supported key (case-insensitive).</summary>
    public static bool IsSupported(string? purpose) =>
        purpose != null && All.Contains(purpose.Trim().ToUpperInvariant(), StringComparer.Ordinal);
}

