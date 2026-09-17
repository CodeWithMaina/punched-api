namespace PunchedApi.Domain.Entities;

/// <summary>
/// Lifecycle status of a <see cref="LoyaltyEarningRule"/>.
/// Draft → Active ↔ Inactive, and Archived (terminal).
/// Only <see cref="Active"/> rules award stamps.
/// </summary>
public enum EarningRuleStatus
{
    /// <summary>Being configured; never awards stamps.</summary>
    Draft = 0,

    /// <summary>Live — the rule participates in manual and automatic earning.</summary>
    Active = 1,

    /// <summary>Switched off by the owner; retained for editing, awards nothing.</summary>
    Inactive = 2,

    /// <summary>Terminal state; retained for history, awards nothing.</summary>
    Archived = 3
}
