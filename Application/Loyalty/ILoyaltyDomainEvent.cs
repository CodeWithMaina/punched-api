namespace PunchedApi.Application.Loyalty;

/// <summary>
/// Marker for a domain event another module publishes for Loyalty to react to.
/// Producers (Appointments, Service Catalog, Referrals) own the event and the
/// state they changed; Loyalty owns what happens next. Publishers never call
/// into Loyalty's internals.
/// </summary>
public interface ILoyaltyDomainEvent
{
    /// <summary>
    /// The tenant the event belongs to. Always server-resolved from the owning
    /// module's own record — never supplied by a client.
    /// </summary>
    Guid BusinessId { get; }

    /// <summary>UTC timestamp when the qualifying state change was committed.</summary>
    DateTime OccurredAt { get; }
}

/// <summary>
/// Published after an appointment transitions to "completed".
/// Only a completed appointment qualifies — booked/confirmed/cancelled/no-show do not.
/// </summary>
public sealed record AppointmentCompletedEvent(
    Guid AppointmentId,
    Guid BusinessId,
    Guid CustomerId,
    Guid? StaffUserId,
    IReadOnlyList<Guid> ServiceIds,
    DateTime OccurredAt) : ILoyaltyDomainEvent;

/// <summary>
/// Published after a referral becomes successful (the referred customer
/// completed their first qualifying appointment). The loyalty stamp belongs to
/// the REFERRER, never to the referred customer.
/// </summary>
public sealed record ReferralCompletedEvent(
    Guid ReferralId,
    Guid BusinessId,
    Guid ReferrerId,
    Guid RefereeId,
    DateTime OccurredAt) : ILoyaltyDomainEvent;

/// <summary>
/// A Loyalty-side reaction to a domain event. Implementations are resolved from
/// DI and invoked by <see cref="ILoyaltyEventBus"/>.
/// </summary>
public interface ILoyaltyEventHandler
{
    /// <summary>Whether this handler reacts to the given event.</summary>
    bool CanHandle(ILoyaltyDomainEvent domainEvent);

    /// <summary>Reacts to the event. Implementations must be idempotent.</summary>
    Task HandleAsync(ILoyaltyDomainEvent domainEvent, CancellationToken cancellationToken = default);
}

/// <summary>
/// Minimal in-process, post-commit dispatcher. Deliberately not a message
/// broker: it matches this codebase's direct-injection style and keeps the
/// publish site transactional-free, so a Loyalty failure can never roll back
/// the owning module's committed work.
/// </summary>
public interface ILoyaltyEventBus
{
    /// <summary>
    /// Dispatches the event to every handler that handles it. Handler failures
    /// are logged and swallowed so the producer is never broken by a consumer.
    /// </summary>
    Task PublishAsync(ILoyaltyDomainEvent domainEvent, CancellationToken cancellationToken = default);
}