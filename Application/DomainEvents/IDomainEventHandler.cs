namespace PunchedApi.Application.DomainEvents;

/// <summary>
/// One handler of persisted domain events. Implementations are resolved from DI by
/// the relay worker and are required to be idempotent: handler execution is
/// checkpointed through the <c>handledByJson</c> array on the event row, so a crash
/// and retry never re-runs a completed handler.
/// </summary>
public interface IDomainEventHandler
{
    /// <summary>
    /// Whether this handler is interested in the event type (e.g. <c>appointment.completed</c>).
    /// </summary>
    bool CanHandle(string eventType);

    /// <summary>
    /// Reacts to the event. Failures are logged — a handler must never break the
    /// relay or its sibling handlers — and are retryable via the event row.
    /// </summary>
    Task HandleAsync(DomainEventRecord record, CancellationToken ct = default);
}