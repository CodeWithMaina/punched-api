namespace PunchedApi.Application.Notifications;

/// <summary>
/// The system default preference matrix (code, never a table). Consulted only
/// when neither the business nor the user has an overriding row, which is what
/// keeps <c>notification_preferences</c> sparse.
/// </summary>
/// <remarks>
/// Matrix (architecture doc §7):
/// <list type="bullet">
///   <item>APPOINTMENT / LOYALTY / PAYMENT / SECURITY / SYSTEM → in_app + email + push ON.</item>
///   <item>MARKETING → OFF for every channel until the user explicitly opts in (R3).</item>
///   <item>sms → OFF for every category until the user opts in per category (R3, cost gate).</item>
/// </list>
/// <c>push</c> resolves ON for transactional categories but is phase-gated: until a
/// <see cref="INotificationChannel"/> named <c>push</c> is registered, the resolved
/// view reports it as unavailable so the client greys the row instead of offering a lie.
/// </remarks>
public static class NotificationDefaults
{
    /// <summary>Every channel the platform knows about (used to build the resolved view).</summary>
    public static readonly IReadOnlyList<string> AllChannels = new[]
    {
        NotificationChannel.InApp,
        NotificationChannel.Email,
        NotificationChannel.Sms,
        NotificationChannel.Push
    };

    /// <summary>Every category a client may render preferences for.</summary>
    public static readonly IReadOnlyList<string> AllCategories = new[]
    {
        NotificationCategory.Appointment,
        NotificationCategory.Loyalty,
        NotificationCategory.Payment,
        NotificationCategory.Security,
        NotificationCategory.Marketing,
        NotificationCategory.System
    };

    /// <summary>The system default for one (category, channel) pair.</summary>
    public static bool IsEnabled(string category, string channel)
    {
        // R3 — MARKETING is off everywhere until the user opts in.
        if (category == NotificationCategory.Marketing) return false;

        // R3 — sms is opt-in only, for cost control.
        if (channel == NotificationChannel.Sms) return false;

        return channel is NotificationChannel.InApp
            or NotificationChannel.Email
            or NotificationChannel.Push;
    }
}
