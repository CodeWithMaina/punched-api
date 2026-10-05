using System.ComponentModel.DataAnnotations;

namespace PunchedApi.Domain.Entities;

public class ServiceCatalogItem : BaseEntity
{
    [Required]
    public Guid BusinessId { get; set; }

    [Required]
    [MaxLength(120)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional customer-facing description shown during booking.</summary>
    [MaxLength(500)]
    public string? Description { get; set; }

    public int DurationMinutes { get; set; }
    public decimal? Price { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Whether this service is surfaced on PUBLIC surfaces (the storefront
    /// catalogue and the booking wizard's service step).
    ///
    /// <para><b>Distinct from <see cref="IsActive"/>.</b> <c>IsActive</c> means
    /// "the business still offers this"; <c>Showcase</c> means "and we are
    /// advertising it right now". A service can be live but unadvertised (a
    /// seasonal special), or advertised but paused (a stock-out). Defaulting
    /// to <c>true</c> preserves the behaviour every row had before this
    /// column existed, so adding it cannot silently empty the storefront.</para>
    /// </summary>
    public bool Showcase { get; set; } = true;
}
