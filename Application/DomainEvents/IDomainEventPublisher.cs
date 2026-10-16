namespace PunchedApi.Application.DomainEvents;

/// <summary>
/// The durable event relay pipeline interface. Publishing emits a row into
/// <c>domain_events</c>, which the <see cref="DomainEventRelayWorker"/> picks up and
/// dispatches to in-process <see cref="IDomainEventHandler"/> implementations.
/// Row + state change commit together (intent-level durability), so a crash after
/// persist cannot lose the event.
/// </summary>
public interface IDomainEventPublisher
{
    /// <summary>
    /// Persists the event inside the caller's unit of work (call BEFORE the
    /// caller's SaveChanges). See <see cref="DomainEventRelayWorker"/> for the
    /// relay-side delivery and checkpoint bookkeeping.
    /// </summary>
    Task PublishAsync(DomainEventRecord record, CancellationToken ct = default);
}