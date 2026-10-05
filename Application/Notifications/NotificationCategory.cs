namespace PunchedApi.Application.Notifications;

/// <summary>
/// Notification categories — the *user-facing* preference granularity.
/// Declared as <c>const string</c> (not an enum) because
/// <c>notification_preferences.category</c> is a validated <c>varchar</c>:
/// categories are the only notification concept stored in the database, so
/// adding a new event type to an existing category needs no migration.
/// </summary>
public static class NotificationCategory
{
    public const string Appointment = "APPOINTMENT";
    public const string Loyalty = "LOYALTY";
    public const string Payment = "PAYMENT";
    public const string Security = "SECURITY";
    public const string Marketing = "MARKETING";
    public const string System = "SYSTEM";
}
