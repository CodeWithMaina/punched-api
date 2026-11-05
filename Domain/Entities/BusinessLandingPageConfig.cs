namespace PunchedApi.Domain.Entities;

/// <summary>
/// Per-business controlled landing-page configuration (presentation, visibility,
/// ordering and overrides only). Domain data (services, loyalty, bookings,
/// reviews, business profile, gallery) stays in its owning module.
/// Absence of a row means platform defaults apply.
/// </summary>
public class BusinessLandingPageConfig : BaseEntity
{
    /// <summary>Owning business. Also the primary key.</summary>
    public Guid BusinessId { get; set; }

    /// <summary>Optimistic concurrency token (owner PUT must match).</summary>
    public int Version { get; set; }

    /// <summary>UTC timestamp of the last owner save.</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Hero background media (business-cover purpose). Null means the default
    /// storefront photograph. Real FK with SET NULL so deleting media falls back.
    /// </summary>
    public Guid? HeroBackgroundMediaId { get; set; }

    /// <summary>Validated configuration payload (nav/sections/ctas/overrides) as JSON.</summary>
    public string ConfigJson { get; set; } = string.Empty;

    // ── Storefront Builder (rich, section-based) ───────────────────────────────
    // The columns below hold the Storefront Builder's richer configuration. They
    // are additive: a row with no builder config behaves exactly as before (the
    // legacy simple landing page built from ConfigJson). The builder keeps a
    // strict draft/published split so an in-progress edit can never corrupt the
    // live storefront.

    /// <summary>Selected template id (e.g. "classic"). Empty = platform default.</summary>
    public string TemplateId { get; set; } = string.Empty;

    /// <summary>
    /// The builder's working draft as JSON (<c>StorefrontConfigDto</c>). Null means
    /// the owner has never opened the builder; the draft is then materialised from
    /// the published config (or platform defaults) on first read.
    /// </summary>
    public string? DraftConfigJson { get; set; }

    /// <summary>
    /// The builder's live configuration as JSON (<c>StorefrontConfigDto</c>). Null
    /// means the rich builder has never been published for this business, and the
    /// public storefront falls back to the legacy landing page.
    /// </summary>
    public string? PublishedConfigJson { get; set; }

    /// <summary>
    /// Whether the business's storefront is publicly published. Defaults to true so
    /// existing businesses (and businesses that never touch the builder) stay live.
    /// </summary>
    public bool IsPublished { get; set; } = true;

    /// <summary>UTC timestamp of the last successful publish (null = never published via the builder).</summary>
    public DateTime? PublishedAt { get; set; }

    /// <summary>Version counter of the last published configuration.</summary>
    public int PublishedVersion { get; set; }

    public virtual Business Business { get; set; } = null!;
    public virtual Media? HeroBackgroundMedia { get; set; }
}
