using System.Text.RegularExpressions;
using PunchedApi.Application.Notifications;

namespace PunchedApi.Tests.Notifications;

public class NotificationTypeRegistryTests
{
    [Fact]
    public void Every_registered_type_has_email_and_in_app_templates()
    {
        // 33 registered types: the original 26 plus the reschedule
        // withdrawn/expired, staff-assigned and commission lifecycle types added
        // with the rescheduling and commission work, plus `storefront.enquiry`.
        // Every type that names Email as a default channel must have both an
        // email and an in-app template (asserted below); in-app-only types
        // (`loyalty.win_back_nudge`, `storefront.enquiry`) are exempt by design.
        Assert.Equal(33, NotificationTypes.All.Count);
        foreach (var definition in NotificationTypes.All.Where(definition => definition.DefaultChannels.Contains(NotificationChannel.Email)))
        {
            // SECURITY (the verification code) is email-only by design (defect
            // register D21): the code must not be persisted into the in-app inbox
            // payload or rendered into SMS/push, and an unverified recipient has no
            // inbox to read it from anyway.
            var isSecurity = definition.Category == NotificationCategory.Security;

            Assert.Contains(NotificationChannel.Email, definition.DefaultChannels);
            Assert.False(string.IsNullOrWhiteSpace(definition.TitleTemplate));
            Assert.False(string.IsNullOrWhiteSpace(definition.BodyTemplate));
            Assert.NotEmpty(NotificationTemplates.Get(definition.Name, NotificationChannel.Email).Subject);

            if (!isSecurity)
            {
                Assert.Contains(NotificationChannel.InApp, definition.DefaultChannels);
                Assert.NotEmpty(NotificationTemplates.Get(definition.Name, NotificationChannel.InApp).Body);
            }

            var tokens = Regex.Matches(definition.TitleTemplate + definition.BodyTemplate, @"\{\{([^{}]+)\}\}")
                .Select(match => match.Groups[1].Value);
            Assert.All(tokens, token => Assert.Contains(token,
                new[]
                {
                    "businessName", "scheduledAt", "stamps", "amount", "code", "rewardName", "appointmentId",
                    "customerName", "daysInactive", "renewalDate", "balance", "cardName",
                    "BookLink", "BillingLink", "NotebookLink",
                    // Added with the commission lifecycle notifications: both are
                    // registered payload keys (commission.adjusted requires
                    // "reason"; commission.payout_recorded requires "reference").
                    "reason", "reference",
                    // Added with storefront.enquiry: both are registered payload
                    // keys of that type (its schema requires "topic" and accepts
                    // "visitorName").
                    "topic", "visitorName"
                }));
        }
    }

    [Fact]
    public void Security_verification_code_is_email_only()
    {
        var definition = NotificationTypes.Get("security.verification_code");

        Assert.Single(definition.DefaultChannels);
        Assert.Contains(NotificationChannel.Email, definition.DefaultChannels);
        Assert.DoesNotContain(NotificationChannel.InApp, definition.DefaultChannels);
        Assert.DoesNotContain(NotificationChannel.Sms, definition.DefaultChannels);
        Assert.DoesNotContain(NotificationChannel.Push, definition.DefaultChannels);
    }

    [Theory]
    [InlineData("GoalReached", "loyalty.goal_reached")]
    [InlineData("RewardReady", "loyalty.reward_ready")]
    [InlineData("RescheduleRequested", "appointment.reschedule_requested")]
    [InlineData("RescheduleApproved", "appointment.reschedule_approved")]
    [InlineData("RescheduleRejected", "appointment.reschedule_rejected")]
    [InlineData("RewardFulfilled", "loyalty.reward_fulfilled")]
    [InlineData("RewardCancelled", "loyalty.reward_cancelled")]
    public void Legacy_names_resolve_to_registered_canonical_types(string legacy, string canonical)
    {
        Assert.Equal(canonical, NotificationTypeAliases.Resolve(legacy));
        Assert.NotNull(NotificationTypes.Get(canonical));
    }

    [Fact]
    public void Unknown_alias_is_preserved()
    {
        Assert.Equal("future.type", NotificationTypeAliases.Resolve("future.type"));
    }
}