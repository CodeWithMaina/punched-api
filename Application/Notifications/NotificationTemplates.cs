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
        Add(templates, "appointment.reminder", "Appointment reminder", "Appointment reminder", "Reminder: your appointment with {{businessName}} is scheduled for {{scheduledAt}}.");
        Add(templates, "appointment.completed", "Appointment completed", "Appointment completed", "Thanks for visiting {{businessName}} on {{scheduledAt}}. We hope to see you again soon.");
        Add(templates, "appointment.no_show_notice", "Missed appointment", "Missed appointment", "You missed your appointment with {{businessName}} scheduled for {{scheduledAt}}. Reply to rebook.");
        Add(templates, "loyalty.stamp_received", "Stamp collected", "Stamp collected", "You collected a stamp at {{businessName}}. You now have {{stamps}}.");
        Add(templates, "loyalty.goal_reached", "Daily goal reached", "Daily goal reached", "You collected {{stamps}} stamps at {{businessName}} and reached your daily goal.");
        Add(templates, "loyalty.reward_ready", "Reward ready", "Reward ready", "You have a reward ready to claim at {{businessName}}.");
        Add(templates, "loyalty.reward_fulfilled", "Reward fulfilled", "Reward fulfilled", "Your {{rewardName}} reward from {{businessName}} has been fulfilled.");
        Add(templates, "loyalty.reward_cancelled", "Reward cancelled", "Reward cancelled", "Your {{rewardName}} reward from {{businessName}} was cancelled.");
        Add(templates, "loyalty.card_corrected", "Card updated", "Card updated", "Your loyalty card at {{businessName}} was updated.");
        Add(templates, "appointment.follow_up", "Follow-up reminder", "Follow-up reminder", "Your follow-up with {{businessName}} is due at {{scheduledAt}}. {{BookLink}}");
        Add(templates, "subscription.expiring", "Subscription expiring", "Subscription expiring", "Your subscription at {{businessName}} renews on {{renewalDate}}. {{BillingLink}}");
        Add(templates, "customer.lapsed", "We miss you", "We miss you at {{businessName}}", "It's been {{daysInactive}} days since your last visit to {{businessName}}. We'd love to see you again — book your next appointment.");
        Add(templates, "loyalty.win_back_nudge", "We miss you", "We miss you at {{businessName}}", "You have {{stamps}} stamps at {{businessName}} — come collect your next one.");
        Add(templates, "payment.failed", "Payment failed", "Payment failed", "We could not confirm your payment of {{amount}} to {{businessName}}. Please try again or contact the business.");
        Add(templates, "payment.reversed", "Payment reversed", "Payment reversed", "Your payment of {{amount}} to {{businessName}} has been reversed.");
        Add(templates, "loyalty.reward_awarded", "Reward awarded", "Reward awarded", "You just earned a reward at {{businessName}}: {{rewardName}}. {{NotebookLink}}");
        Add(templates, "loyalty.burn", "Reward redeemed", "Reward redeemed", "You redeemed {{rewardName}} at {{businessName}}.");
        Add(templates, "stamp_awarded", "Stamp collected", "Stamp collected", "You collected a new stamp at {{businessName}}. {{cardName}}");
        Add(templates, "balance_changed", "Balance updated", "Balance updated", "Your loyalty balance at {{businessName}} changed to {{balance}}.");
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