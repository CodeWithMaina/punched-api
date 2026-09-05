namespace PunchedApi.Domain.Entities;

/// <summary>
/// Lifecycle status of a <see cref="StampCard"/>.
/// Draft → Active ↔ Inactive, and Archived (terminal).
/// </summary>
public enum StampCardStatus
{
    /// <summary>Being configured; not yet usable by customers.</summary>
    Draft = 0,

    /// <summary>Live — selectable/enrollable within its campaign.</summary>
    Active = 1,

    /// <summary>Temporarily disabled by the owner; can be reactivated.</summary>
    Inactive = 2,

    /// <summary>Terminal state; hidden from customer surfaces, retained for history.</summary>
    Archived = 3
}
