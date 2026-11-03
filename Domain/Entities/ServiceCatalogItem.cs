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

    /// <summary>
    /// Optional business-scoped grouping. Null means "uncategorised", which is
    /// the state every pre-existing service keeps after the category migration —
    /// no service is silently filed under a guessed category.
    /// </summary>
    public Guid? CategoryId { get; set; }

    /// <summary>
    /// Whether the business has filed this service away permanently (the
    /// "archive" action). An archived service is never bookable and never shown
    /// on a public surface, and it keeps its history — unlike a delete, which
    /// also deactivates it but is meant to be final.
    ///
    /// <para>Archiving always writes <c>IsActive = false</c>; restoring clears
    /// this flag and reactivates. <c>IsArchived</c> is therefore only ever
    /// meaningful together with <see cref="IsActive"/> = false, which is what
    /// <see cref="ServiceCatalogStatuses.From"/> encodes.</para>
    /// </summary>
    public bool IsArchived { get; set; }

    public virtual ServiceCategory? Category { get; set; }
}

/// <summary>
/// The three owner-facing service states, derived from the two persisted flags.
///
/// <para><c>IsActive</c> answers "do we still offer this?"; <c>Showcase</c>
/// answers "are we advertising it?"; <c>IsArchived</c> answers "have we filed it
/// away?". Only the first and third decide the status, so a paused-but-advertised
/// service stays <c>paused</c> rather than flipping to a fourth state.</para>
/// </summary>
public static class ServiceCatalogStatuses
{
    public const string Active = "active";
    public const string Paused = "paused";
    public const string Archived = "archived";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Active, Paused, Archived
    };

    /// <summary>Archived wins over paused: a filed-away service is never merely paused.</summary>
    public static string From(bool isActive, bool isArchived) =>
        isArchived ? Archived : isActive ? Active : Paused;

    /// <summary>
    /// Translates a status back into the persisted flags. Unknown values return
    /// null so a caller can reject them instead of guessing a state.
    /// </summary>
    public static (bool IsActive, bool IsArchived)? ToFlags(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        Active => (true, false),
        Paused => (false, false),
        Archived => (false, true),
        _ => null
    };
}
