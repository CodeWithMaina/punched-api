namespace PunchedApi.Application.Notifications;

public sealed record NotificationTemplate(string Subject, string Title, string Body);

public static class NotificationTemplates
{
    private static readonly IReadOnlyDictionary<(string Type, string Channel), NotificationTemplate> Templates = Build();

    private static IReadOnlyDictionary<(string Type, string Channel), NotificationTemplate> Build()
    {
        var templates = new Dictionary<(string Type, string Channel), NotificationTemplate>();
        Add(templates, "appointment.booked", "Appointment booked", "Appointment booked", "Your appointment with {{businessName}} is booked for {{scheduledAt}}.");
        Add(templates, "appointment.cancelled", "Appointment cancelled", "Appointment cancelled", "Your appointment with {{businessName}} on {{scheduledAt}} was cancelled.");
        Add(templates, "appointment.reschedule_requested", "Reschedule request received", "Reschedule requested", "A reschedule was requested for {{businessName}} at {{scheduledAt}}.");
        Add(templates, "appointment.reschedule_approved", "Reschedule approved", "Reschedule approved", "Your appointment with {{businessName}} is now scheduled for {{scheduledAt}}.");
        Add(templates, "appointment.reschedule_rejected", "Reschedule declined", "Reschedule declined", "Your reschedule request for {{businessName}} at {{scheduledAt}} was declined.");
        Add(templates, "loyalty.stamp_received", "Stamp collected", "Stamp collected", "You collected a stamp at {{businessName}}. You now have {{stamps}}.");
        Add(templates, "loyalty.goal_reached", "Daily goal reached", "Daily goal reached", "You collected {{stamps}} stamps at {{businessName}} and reached your daily goal.");
        Add(templates, "loyalty.reward_ready", "Reward ready", "Reward ready", "You have a reward ready to claim at {{businessName}}.");
        Add(templates, "loyalty.reward_fulfilled", "Reward fulfilled", "Reward fulfilled", "Your {{rewardName}} reward from {{businessName}} has been fulfilled.");
        Add(templates, "loyalty.reward_cancelled", "Reward cancelled", "Reward cancelled", "Your {{rewardName}} reward from {{businessName}} was cancelled.");
        Add(templates, "loyalty.card_corrected", "Card updated", "Card updated", "Your loyalty card at {{businessName}} was updated.");
        Add(templates, "loyalty.win_back_nudge", "We miss you", "We miss you", "You are {{stamps}} stamps away from your next reward at {{businessName}}.");
        Add(templates, "loyalty.stamp_expired", "Stamp progress expired", "Stamp progress expired", "Your stamp progress at {{businessName}} has expired.");
        Add(templates, "payment.success", "Payment received", "Payment received", "We received your payment of {{amount}} to {{businessName}}.");
        Add(templates, "security.verification_code", "Your verification code", "Your verification code", "Your verification code is {{code}}. Do not share it with anyone.");
        return templates;
    }

    private static void Add(
        IDictionary<(string Type, string Channel), NotificationTemplate> templates,
        string type,
        string subject,
        string title,
        string body)
    {
        var template = new NotificationTemplate(subject, title, body);
        templates[(type, NotificationChannel.Email)] = template;
        templates[(type, NotificationChannel.InApp)] = template;
        templates[(type, NotificationChannel.Sms)] = template;
        templates[(type, NotificationChannel.Push)] = template;
    }

    public static NotificationTemplate Get(string type, string channel)
    {
        if (Templates.TryGetValue((type, channel), out var template))
            return template;

        throw new ArgumentException($"No {channel} template is registered for '{type}'.", nameof(type));
    }
}