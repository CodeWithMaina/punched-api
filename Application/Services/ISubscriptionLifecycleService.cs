using PunchedApi.Domain.Entities;

namespace PunchedApi.Application.Services;

/// <summary>
/// Owns the subscription lifecycle (G4): plan changes, expiry, renewal and
/// cancellation. Every mutation invalidates the business's entitlement cache
/// via <see cref="IModuleEntitlementService.Invalidate"/> so module access
/// reflects the new subscription state immediately.
/// </summary>
public interface ISubscriptionLifecycleService
{
    /// <summary>
    /// Upserts the business's one active subscription: marks any current
    /// active/trial row canceled and adds a new active row on the target plan
    /// (StartsAt=now, EndsAt derived from the plan's billing interval).
    /// </summary>
    /// <returns>The new active subscription row.</returns>
    Task<BusinessSubscription> ChangePlanAsync(Guid businessId, Guid planId, Guid? actorUserId, string? reason = null);

    /// <summary>Active/trial subscriptions → expired (EndsAt set if null).</summary>
    Task<int> ExpireAsync(Guid businessId);

    /// <summary>
    /// Expired/past_due/canceled subscriptions → active, extending EndsAt
    /// (or by one billing interval of the current plan when not supplied).
    /// </summary>
    Task<int> RenewAsync(Guid businessId, DateTime? newEndsAt = null);

    /// <summary>Active/trial subscriptions → canceled (CanceledAt set).</summary>
    Task<int> CancelAsync(Guid businessId, string? reason);

    /// <summary>
    /// Active/trial/past_due subscriptions → paused. Module access is withheld
    /// immediately (entitlement resolution only grants access for active/trial)
    /// while the row, its dates and its history are retained. Reversible via
    /// <see cref="ResumeAsync"/>.
    /// </summary>
    /// <returns>The number of rows moved to paused.</returns>
    Task<int> PauseAsync(Guid businessId, string? reason, Guid? actorUserId);

    /// <summary>
    /// Paused subscriptions → active. If the period already elapsed while paused,
    /// <c>EndsAt</c> is extended by one billing interval of the current plan so the
    /// business is not immediately expired on resume.
    /// </summary>
    /// <returns>The number of rows resumed.</returns>
    Task<int> ResumeAsync(Guid businessId, Guid? actorUserId);

    /// <summary>
    /// Extends the subscription's <c>EndsAt</c>. A subscription whose period has
    /// lapsed (expired/canceled/past_due/paused) is reactivated, because extending
    /// it is only meaningful if it grants access again. Never shortens the period.
    /// </summary>
    /// <returns>The number of rows extended.</returns>
    Task<int> ExtendAsync(Guid businessId, DateTime newEndsAt, Guid? actorUserId, string? reason);

    /// <summary>
    /// Active/trial/past_due/paused subscriptions → deactivated. An administrative
    /// closure of the subscription only (the business itself is a separate axis);
    /// reversible via <see cref="ReactivateAsync"/> or <see cref="ChangePlanAsync"/>.
    /// </summary>
    /// <returns>The number of rows deactivated.</returns>
    Task<int> DeactivateAsync(Guid businessId, string? reason, Guid? actorUserId);

    /// <summary>
    /// Any withheld subscription (expired, past_due, canceled, paused or
    /// deactivated) → active. Unlike <see cref="RenewAsync"/> — which is the
    /// billing-led extension of a lapsed period — this is the administrative
    /// "bring it back" action and covers every state that withholds access.
    /// </summary>
    /// <returns>The number of rows reactivated.</returns>
    Task<int> ReactivateAsync(Guid businessId, Guid? actorUserId);
}
