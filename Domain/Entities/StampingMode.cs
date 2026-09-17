namespace PunchedApi.Domain.Entities;

/// <summary>
/// How stamps are awarded for an <see cref="LoyaltyEarningRule"/>.
/// This is an actual earning strategy, not a cosmetic boolean:
/// <see cref="Manual"/> requires a staff/business action, <see cref="Automatic"/>
/// awards stamps when the owning module publishes the qualifying domain event.
/// </summary>
public enum StampingMode
{
    /// <summary>
    /// Default. Staff award stamps explicitly after the qualifying action
    /// (e.g. from the customer's loyalty card or the scan console).
    /// Automatic event processing never awards stamps for this rule.
    /// </summary>
    Manual = 0,

    /// <summary>
    /// The system awards stamps automatically when the qualifying domain event
    /// is published (appointment completed / service completed / referral completed).
    /// Applies from activation forward — never retroactively.
    /// </summary>
    Automatic = 1
}
