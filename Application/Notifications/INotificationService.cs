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
}
