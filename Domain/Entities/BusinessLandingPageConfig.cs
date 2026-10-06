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

    public virtual Business Business { get; set; } = null!;
    public virtual Media? HeroBackgroundMedia { get; set; }
}
