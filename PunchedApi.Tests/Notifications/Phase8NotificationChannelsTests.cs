using PunchedApi.Application.Notifications;

namespace PunchedApi.Tests.Notifications;

public class Phase8NotificationChannelsTests
{
    [Fact]
    public void All_transactional_types_include_push_in_the_default_channel_set()
    {
        foreach (var definition in NotificationTypes.All.Where(definition => definition.DefaultChannels.Contains(NotificationChannel.Email)))
        {
            Assert.Contains(NotificationChannel.Push, definition.DefaultChannels);
        }
    }

    [Fact]
    public void Push_and_sms_templates_are_available_for_all_registered_types()
    {
        foreach (var definition in NotificationTypes.All)
        {
            var pushTemplate = NotificationTemplates.Get(definition.Name, NotificationChannel.Push);
            var smsTemplate = NotificationTemplates.Get(definition.Name, NotificationChannel.Sms);

            Assert.False(string.IsNullOrWhiteSpace(pushTemplate.Title));
            Assert.False(string.IsNullOrWhiteSpace(pushTemplate.Body));
            Assert.False(string.IsNullOrWhiteSpace(smsTemplate.Subject));
            Assert.False(string.IsNullOrWhiteSpace(smsTemplate.Body));
        }
    }
}
