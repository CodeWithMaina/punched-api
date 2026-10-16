namespace PunchedApi.Application.DomainEvents;

/// <summary>
/// One persisted domain event: the same fields the relay worker dispatches on.
/// Payload is the serialized state change; occurred_at_utc is the authoritative
/// UTC timestamp of the qualifying state change (never client-supplied).
/// </summary>
public sealed record DomainEventRecord(
    string EventType,
    string? AggregateType,
    Guid? AggregateId,
    Guid? BusinessId,
    string PayloadJson,
    DateTime OccurredAtUtc);