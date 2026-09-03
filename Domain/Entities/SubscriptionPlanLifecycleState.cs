namespace PunchedApi.Domain.Entities;

/// <summary>
/// Lifecycle state of a subscription tier (plan). Drives whether a tier
/// is creatable, editable, publishable, deactivatable, or archivable.
/// Mirrored to the legacy <c>IsActive</c> convenience column: Active ⇔ true.
/// </summary>
public enum SubscriptionPlanLifecycleState
{
    /// <summary>New/draft tier. Modules editable. Not assignable. Saved manually.</summary>
    Draft = 0,

    /// <summary>Published, live tier. Assignable. Modules read-only.</summary>
    Active = 1,

    /// <summary>Deactivated tier. Modules editable. Not assignable.</summary>
    Inactive = 2,

    /// <summary>Archived (terminal) tier. Not editable or assignable.</summary>
    Archived = 3,
}