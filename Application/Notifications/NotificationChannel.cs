namespace PunchedApi.Application.Notifications;

/// <summary>
/// Delivery channels, declared as <c>const string</c> because
/// <c>notification_preferences.channel</c> and <c>notifications.channel</c>
/// store the value as <c>varchar</c>.
/// </summary>
/// <remarks>
/// <see cref="InApp"/> is written inline by
/// <see cref="NotificationService.SendAsync"/> — it has no
/// <see cref="INotificationChannel"/> implementation (the inbox INSERT *is* the
/// delivery). Every other channel is one class behind
/// <see cref="INotificationChannel"/> plus one <c>AddScoped</c> line.
/// </remarks>
public static class NotificationChannel
{
    public const string InApp = "in_app";
    public const string Email = "email";
    public const string Sms = "sms";
    public const string Push = "push";
}
