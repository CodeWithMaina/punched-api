using System.ComponentModel.DataAnnotations;

namespace PunchedApi.Domain.Entities;

/// <summary>
/// A business-scoped grouping of catalogue services (Haircuts, Beard grooming,
/// Custom tattoos, Massages, ...).
///
/// <para>Categories are <b>per business and free-form</b> — the platform never
/// imposes salon-specific (or barbershop/tattoo/spa-specific) labels globally.
/// Businesses may optionally be given starter categories derived from their
/// declared business type at onboarding, and may rename, reorder, deactivate
/// or delete them afterwards.</para>
///
/// <para><b>Deactivation vs deletion.</b> Deactivating a category keeps every
/// service assigned to it and keeps the assignment resolvable (so historical
/// data and the owner console stay intact) while hiding the group from public
/// grouping surfaces. Deleting is only allowed while nothing references the
/// category — enforced in the service and protected at the database level by a
/// restricted foreign key from <see cref="ServiceCatalogItem.CategoryId"/>.</para>
/// </summary>
public class ServiceCategory : BaseEntity
{
    [Required]
    public Guid BusinessId { get; set; }

    [Required]
    [MaxLength(80)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(300)]
    public string? Description { get; set; }

    /// <summary>Owner-controlled display order (ascending).</summary>
    public int SortOrder { get; set; }

    /// <summary>False hides the category from public grouping; assignments survive.</summary>
    public bool IsActive { get; set; } = true;

    [Required]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public virtual Business Business { get; set; } = null!;
}
