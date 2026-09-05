using System.ComponentModel.DataAnnotations;

namespace PunchedApi.Domain.Entities;

/// <summary>
/// A reusable HTML template controlling the visual presentation of stamp cards.
/// Owned by a business so the same visual identity can be assigned to many
/// <see cref="StampCard"/>s. Templates are stored *after* server-side
/// sanitization (whitelist-based) and rendered exclusively through the
/// controlled template variable pipeline — they can never execute code.
/// </summary>
public class CardDesign : BaseEntity
{
    /// <summary>FK to the owning Business.</summary>
    [Required]
    public Guid BusinessId { get; set; }

    /// <summary>Display name (e.g. "Modern", "Christmas"). Max 100 chars.</summary>
    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = "Card Design";

    /// <summary>
    /// Sanitized HTML template body (fragment, not a full document). Max 50,000 chars.
    /// See <c>PunchedApi.Application.Services.CardTemplateSanitizer</c>.
    /// </summary>
    [Required]
    [MaxLength(50000)]
    public string HtmlTemplate { get; set; } = string.Empty;

    /// <summary>Whether this design may be assigned to new stamp cards.</summary>
    public bool IsActive { get; set; } = true;

    // ── Navigation ──────────────────────────────────────────
    public virtual Business Business { get; set; } = null!;
    public virtual ICollection<StampCard> StampCards { get; set; } = new List<StampCard>();
}
