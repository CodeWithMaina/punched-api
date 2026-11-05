using WebPush;

namespace PunchedApi.Infrastructure.Notifications;

/// <summary>
/// Thin seam over the Web Push client. The channel depends on this instead of
/// constructing <see cref="WebPushClient"/> directly, which keeps it unit-testable
/// and keeps the HTTP/ECDSA resources the client owns in one disposal scope.
/// </summary>
public interface IPushNotificationSender
{
    /// <summary>
    /// Sends one payload to one subscription. Throws <see cref="WebPushException"/>
    /// (with the provider status code) when the push service rejects the request.
    /// </summary>
    Task SendAsync(
        PushSubscription subscription,
        string payload,
        VapidDetails vapidDetails,
        CancellationToken ct = default);
}

/// <summary>
/// Default sender. A fresh <see cref="WebPushClient"/> is created and disposed per
/// call so no connection pool or crypto material is retained across the worker's
/// long-lived scope.
/// </summary>
public sealed class WebPushNotificationSender : IPushNotificationSender
{
    public async Task SendAsync(
        PushSubscription subscription,
        string payload,
        VapidDetails vapidDetails,
        CancellationToken ct = default)
    {
        using var client = new WebPushClient();
        await client.SendNotificationAsync(subscription, payload, vapidDetails, ct);
    }
}
