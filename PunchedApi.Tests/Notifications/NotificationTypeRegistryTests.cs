using System.Text.RegularExpressions;
using PunchedApi.Application.Notifications;

namespace PunchedApi.Tests.Notifications;

public class NotificationTypeRegistryTests
{
    [Fact]
    public void Every_registered_type_has_email_and_in_app_templates()
    {
        Assert.Equal(15, NotificationTypes.All.Count);
        foreach (var definition in NotificationTypes.All.Where(definition => definition.DefaultChannels.Contains(NotificationChannel.Email)))
        {
            Assert.Contains(NotificationChannel.InApp, definition.DefaultChannels);
            Assert.Contains(NotificationChannel.Email, definition.DefaultChannels);
            Assert.False(string.IsNullOrWhiteSpace(definition.TitleTemplate));
            Assert.False(string.IsNullOrWhiteSpace(definition.BodyTemplate));
            Assert.NotEmpty(NotificationTemplates.Get(definition.Name, NotificationChannel.Email).Subject);
            Assert.NotEmpty(NotificationTemplates.Get(definition.Name, NotificationChannel.InApp).Body);

            var tokens = Regex.Matches(definition.TitleTemplate + definition.BodyTemplate, @"\{\{([^{}]+)\}\}")
                .Select(match => match.Groups[1].Value);
            Assert.All(tokens, token => Assert.Contains(token,
                new[] { "businessName", "scheduledAt", "stamps", "amount", "code", "rewardName", "appointmentId" }));
        }
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