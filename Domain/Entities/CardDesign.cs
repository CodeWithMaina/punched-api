using System.ComponentModel.DataAnnotations;

namespace PunchedApi.Domain.Entities;

/// <summary>
/// A reusable HTML template controlling the visual presentation of loyalty cards.
///
/// Ownership model:
///  • <see cref="BusinessId"/> set  → a business-specific design (Admin-authored,
///    usable only by that business and only while its subscription includes the
///    <c>customCardDesign</c> module).
///  • <see cref="BusinessId"/> null → the platform/system design. Exactly one row
///    has <see cref="IsDefault"/> = true and it is what every loyalty program
///    falls back to when no custom design is selected or the entitlement is gone.
///
/// Templates are stored *after* server-side sanitization (whitelist-based) and
/// rendered exclusively through the controlled template variable pipeline — they
/// can never execute code.
///
/// Subscription state is deliberately NOT stored here (see plan §14): the module
/// entitlement system decides availability, so downgrades are reversible.
/// </summary>
public class CardDesign : BaseEntity
{
    /// <summary>
    /// FK to the owning Business. Null for the platform-wide default design.
    /// </summary>
    public Guid? BusinessId { get; set; }

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

    /// <summary>
    /// True for the single platform-wide fallback design
    /// (<see cref="BusinessId"/> must be null). Enforced by a filtered unique
    /// index plus a check constraint.
    /// </summary>
    public bool IsDefault { get; set; }

    // ── Navigation ──────────────────────────────────────────
    /// <summary>Null for the platform-wide default design.</summary>
    public virtual Business? Business { get; set; }
    public virtual ICollection<StampCard> StampCards { get; set; } = new List<StampCard>();
    public virtual ICollection<LoyaltyProgram> LoyaltyPrograms { get; set; } = new List<LoyaltyProgram>();
}
