namespace PunchedApi.Application.Notifications;

/// <summary>
/// The notification core. Callers only ever call
/// <see cref="SendAsync"/>; they never touch <c>IEmailService</c>, SMTP, an SMS
/// vendor, VAPID or the delivery ledger.
/// </summary>
public interface INotificationService
{
    /// <summary>
    /// Accepts one notification intent: resolves preferences, writes the in-app
    /// inbox row (and its ledger row) inline, and queues external channels.
    /// </summary>
    /// <remarks>
    /// Never throws for provider problems and never throws when no channel
    /// survived — that is a <see cref="NotificationResult"/> with
    /// <see cref="NotificationResult.SuppressReason"/>. It throws only for an
    /// unknown notification type or a tenant-isolation violation.
    /// </remarks>
    /// <exception cref="ArgumentException">Unknown type, or the recipient is not associated with <see cref="NotificationRequest.BusinessId"/>.</exception>
    Task<NotificationResult> SendAsync(NotificationRequest request, CancellationToken ct = default);

    /// <summary>
    /// Accepts one notification intent WITHOUT saving: every row it produces (in-app
    /// inbox, delivery ledger, external outbox) stays tracked in the caller's unit of
    /// work so the caller's single <c>SaveChanges</c> commits the notification intent
    /// atomically with the domain change that caused it (intent-level outbox).
    /// </summary>
    /// <remarks>
    /// Call this BEFORE the caller's <c>SaveChanges</c>, never after — an unsaved intent
    /// is discarded with the caller's transaction. Same acceptance semantics and
    /// exceptions as <see cref="SendAsync"/>; the ledger's idempotency unique index
    /// arbitrates any race at commit time.
    /// </remarks>
    Task<NotificationResult> QueueAsync(NotificationRequest request, CancellationToken ct = default);

    /// <summary>
    /// Queues several intents WITHOUT saving, resolving the tenant guard, the idempotency
    /// keys and the (cached) channel preferences once for the whole batch.
    ///
    /// <para>Identical semantics to <see cref="QueueAsync"/> — every row joins the caller's
    /// unit of work — but the bookkeeping reads are shared, which is what keeps a booking's
    /// two notification intents off the critical path.</para>
    /// </summary>
    Task<IReadOnlyList<NotificationResult>> QueueManyAsync(
        IReadOnlyList<NotificationRequest> requests, CancellationToken ct = default);
}
