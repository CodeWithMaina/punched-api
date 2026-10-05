namespace PunchedApi.Application.Notifications;

/// <summary>
/// The single provider seam. One implementation per EXTERNAL channel;
/// <c>in_app</c> has none because the inbox INSERT inside
/// <see cref="NotificationService.SendAsync"/> is the delivery.
/// </summary>
/// <remarks>
/// Discovered through <c>IEnumerable&lt;INotificationChannel&gt;</c> — the codebase's
/// established multi-implementation pattern (<c>IAttendanceVerifier</c>,
/// <c>ILoyaltyEventHandler</c>, <c>IPaymentProvider</c>). Adding a channel is one
/// class plus one <c>AddScoped</c> line; the worker and the preference view need
/// no edit.
/// </remarks>
public interface INotificationChannel
{
    /// <summary>Channel key, e.g. <c>email</c> — matches <c>notifications.channel</c>.</summary>
    string Name { get; }

    /// <summary>
    /// Delivers one already-decided message. Throwing signals a retryable
    /// failure to the worker; a non-retryable failure must throw a channel-specific
    /// exception the worker recognises.
    /// </summary>
    Task SendAsync(ChannelMessage message, CancellationToken ct = default);
}

/// <summary>One outbox row handed to a channel for delivery.</summary>
/// <param name="OutboxId">The <c>notifications</c> ledger row being delivered.</param>
/// <param name="Type">Registered notification type.</param>
/// <param name="RecipientUserId">The recipient's <c>User.Id</c>.</param>
/// <param name="BusinessId">Tenant context, or null.</param>
/// <param name="Category">Preference category of <paramref name="Type"/>.</param>
/// <param name="Data">Display data used for rendering.</param>
public sealed record ChannelMessage(
    Guid OutboxId,
    string Type,
    Guid RecipientUserId,
    Guid? BusinessId,
    string Category,
    IReadOnlyDictionary<string, object?> Data);
