namespace PunchedApi.Application.Notifications;

/// <summary>
/// One registered notification type. Producers never pass a category or a
/// channel list — they pass <see cref="Name"/> and the registry supplies the
/// category, the default channel candidates and the copy.
/// </summary>
/// <param name="Name">Dotted producer-facing type, e.g. <c>appointment.booked</c>.</param>
/// <param name="Category">Preference granularity — one of <see cref="NotificationCategory"/>.</param>
/// <param name="DefaultChannels">Channel candidates before preferences are applied.</param>
/// <param name="TitleTemplate">Short display title, <c>{{token}}</c> placeholders allowed.</param>
/// <param name="BodyTemplate">Plain-text body, <c>{{token}}</c> placeholders allowed.</param>
public sealed record NotificationTypeDef(
    string Name,
    string Category,
    string[] DefaultChannels,
    string TitleTemplate,
    string BodyTemplate);

/// <summary>
/// The notification type registry — code, not a table. Adding a notification
/// type is one entry here plus one template entry (Phase 4) and zero migrations.
/// </summary>
public static class NotificationTypes
{
    /// <summary>Phase 1 seed: in_app is the only channel that can actually be delivered.</summary>
    public static readonly IReadOnlyList<NotificationTypeDef> All = new NotificationTypeDef[]
    {
        new(
            "appointment.booked",
            NotificationCategory.Appointment,
            new[] { NotificationChannel.InApp },
            "Appointment booked",
            "Your appointment with {{businessName}} is booked for {{scheduledAt}}."),

        new(
            "appointment.cancelled",
            NotificationCategory.Appointment,
            new[] { NotificationChannel.InApp },
            "Appointment cancelled",
            "Your appointment with {{businessName}} on {{scheduledAt}} was cancelled."),

        new(
            "loyalty.stamp_received",
            NotificationCategory.Loyalty,
            new[] { NotificationChannel.InApp },
            "Stamp collected",
            "You collected a stamp at {{businessName}}. You now have {{stamps}}."),

        new(
            "loyalty.reward_ready",
            NotificationCategory.Loyalty,
            new[] { NotificationChannel.InApp },
            "Reward ready",
            "You have a reward ready to claim at {{businessName}}."),

        new(
            "payment.success",
            NotificationCategory.Payment,
            new[] { NotificationChannel.InApp },
            "Payment received",
            "We received your payment of {{amount}} to {{businessName}}."),

        new(
            "security.verification_code",
            NotificationCategory.Security,
            new[] { NotificationChannel.InApp },
            "Your verification code",
            "Your verification code is {{code}}. Do not share it with anyone.")
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
