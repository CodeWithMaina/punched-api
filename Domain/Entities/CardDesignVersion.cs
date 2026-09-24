using System.ComponentModel.DataAnnotations;

namespace PunchedApi.Domain.Entities;

/// <summary>
/// An immutable, append-only snapshot of a <see cref="CardDesign"/>'s
/// *presentation* at a point in time.
///
/// Why this exists: a card design is a presentation asset that a business will
/// change repeatedly (colours, logo, artwork, stamp icons). Without versioning
/// an edit rewrites history — there is no way to see what a customer saw, no way
/// to roll a bad design back, and no way to reason about "published" vs "draft".
///
/// Guarantees:
/// <list type="bullet">
/// <item><b>Append-only.</b> Rows are created on every design write and are never
/// updated. <c>(card_design_id, version_number)</c> is unique.</item>
/// <item><b>Presentation only.</b> A version carries no loyalty data — customer
/// progress, required stamps, rewards and redemption history live elsewhere and
/// are untouched by any version operation (§36, no silent data mutation).</item>
/// <item><b>Independent of loyalty state.</b> Publishing, restoring or
/// deactivating a version can never change <see cref="LoyaltyCard.TotalStamps"/>,
/// <see cref="LoyaltyCard.RequiredStamps"/> or any <see cref="StampTransaction"/>.</item>
/// </list>
/// </summary>
public class CardDesignVersion : BaseEntity
{
    /// <summary>FK to the design this version belongs to.</summary>
    [Required]
    public Guid CardDesignId { get; set; }

    /// <summary>Monotonic, per-design version number starting at 1.</summary>
    [Range(1, int.MaxValue)]
    public int VersionNumber { get; set; }

    /// <summary>Design name as of this version. Max 100 chars.</summary>
    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Sanitized HTML template body as of this version. Max 50,000 chars.</summary>
    [Required]
    [MaxLength(50000)]
    public string HtmlTemplate { get; set; } = string.Empty;

    /// <summary>
    /// Validated presentation configuration as of this version
    /// (<c>PunchedApi.Application.Design.CardDesignConfig</c>), or null when the
    /// version relies purely on the HTML template.
    /// </summary>
    public string? ConfigJson { get; set; }

    /// <summary>When this version was recorded.</summary>
    [Required]
    public DateTime PublishedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Admin/business user that produced this version (null for system writes).</summary>
    public Guid? PublishedByUserId { get; set; }

    /// <summary>Optional human-readable note explaining the change.</summary>
    [MaxLength(500)]
    public string? ChangeNote { get; set; }

    // ── Navigation ──────────────────────────────────────────
    /// <summary>The design this version snapshots.</summary>
    public virtual CardDesign CardDesign { get; set; } = null!;
}
