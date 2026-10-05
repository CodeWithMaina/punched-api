namespace PunchedApi.Application.Notifications;

/// <summary>
/// One registered notification type. Producers never pass a category or a
/// channel list — they pass <see cref="Name"/> and the registry supplies the
/// category, the default channel candidates and the copy.
/// </summary>
/// <param name="Name">Dotted producer-facing type, e.g. <c>appointment.booked</c>.</param>
/// <param name="Category">Preference granularity — one of <see cref="NotificationCategory"/>.</param>
/// <param name="DefaultChannels">Channel candidates before preferences are applied.</param>
public sealed record NotificationTypeDef(
    string Name,
    string Category,
    string[] DefaultChannels)
{
    public string TitleTemplate => NotificationTemplates.Get(Name, NotificationChannel.InApp).Title;
    public string BodyTemplate => NotificationTemplates.Get(Name, NotificationChannel.InApp).Body;
}

/// <summary>
/// The notification type registry — code, not a table. Adding a notification
/// type is one entry here plus one template entry (Phase 4) and zero migrations.
/// </summary>
public static class NotificationTypes
{
    private static readonly string[] Channels =
    {
        NotificationChannel.InApp,
        NotificationChannel.Email,
        NotificationChannel.Sms,
        NotificationChannel.Push
    };
    private static readonly string[] InAppOnly = { NotificationChannel.InApp };

    /// <summary>All supported transactional notification types.</summary>
    public static readonly IReadOnlyList<NotificationTypeDef> All = new NotificationTypeDef[]
    {
        new("appointment.booked", NotificationCategory.Appointment, Channels),
        new("appointment.cancelled", NotificationCategory.Appointment, Channels),
        new("appointment.reschedule_requested", NotificationCategory.Appointment, Channels),
        new("appointment.reschedule_approved", NotificationCategory.Appointment, Channels),
        new("appointment.reschedule_rejected", NotificationCategory.Appointment, Channels),
        new("loyalty.stamp_received", NotificationCategory.Loyalty, Channels),
        new("loyalty.goal_reached", NotificationCategory.Loyalty, Channels),
        new("loyalty.reward_ready", NotificationCategory.Loyalty, Channels),
        new("loyalty.reward_fulfilled", NotificationCategory.Loyalty, Channels),
        new("loyalty.reward_cancelled", NotificationCategory.Loyalty, Channels),
        new("loyalty.card_corrected", NotificationCategory.Loyalty, Channels),
        new("loyalty.win_back_nudge", NotificationCategory.Loyalty, InAppOnly),
        new("loyalty.stamp_expired", NotificationCategory.Loyalty, InAppOnly),
        new("payment.success", NotificationCategory.Payment, Channels),
        new("security.verification_code", NotificationCategory.Security, Channels)
    };

    private static readonly IReadOnlyDictionary<string, NotificationTypeDef> ByName =
        All.ToDictionary(def => def.Name, StringComparer.Ordinal);

    /// <summary>
    /// Looks up a registered type. Throws for an unregistered type instead of
    /// rendering caller-supplied strings (architecture doc §14.4).
    /// </summary>
    /// <exception cref="ArgumentException">The type is not registered.</exception>
    public static NotificationTypeDef Get(string type)
    {
        if (string.IsNullOrEmpty(type) || !ByName.TryGetValue(type, out var def))
        {
            throw new ArgumentException(
                $"Unknown notification type '{type}'. Register it in {nameof(NotificationTypes)}.{nameof(All)}.",
                nameof(type));
        }

        return def;
    }

    /// <summary>Non-throwing lookup used by adapters that must stay tolerant.</summary>
    public static bool TryGet(string? type, out NotificationTypeDef definition)
    {
        if (!string.IsNullOrEmpty(type) && ByName.TryGetValue(type!, out var def))
        {
            definition = def;
            return true;
        }

        definition = null!;
        return false;
    }
}
