using System.Reflection;
using Microsoft.AspNetCore.Mvc.Routing;
using PunchedApi.Application.Notifications;

namespace PunchedApi.Tests.Notifications;

public class NotificationPreferenceApiTests
{
    [Fact]
    public void User_controller_preserves_legacy_and_preference_routes()
    {
        var routes = typeof(API.Controllers.UserController)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .SelectMany(method => method.GetCustomAttributes(true).OfType<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>())
            .SelectMany(attribute => attribute switch
            {
                Microsoft.AspNetCore.Mvc.HttpGetAttribute get => new[] { get.Template },
                Microsoft.AspNetCore.Mvc.HttpPostAttribute post => new[] { post.Template },
                Microsoft.AspNetCore.Mvc.HttpPutAttribute put => new[] { put.Template },
                _ => Array.Empty<string>()
            })
            .ToArray();

        Assert.Contains("notifications", routes);
        Assert.Contains("notifications/{id:guid}/read", routes);
        Assert.Contains("notifications/read-all", routes);
        Assert.Contains("me/notification-preferences", routes);
    }

    [Fact]
    public void Preference_update_binds_business_id_and_rows()
    {
        var request = new Application.DTOs.UpdateNotificationPreferencesRequest
        {
            BusinessId = Guid.NewGuid(),
            Preferences =
            {
                new Application.DTOs.NotificationPreferenceItem
                {
                    Category = NotificationCategory.Loyalty,
                    Channel = NotificationChannel.Email,
                    Enabled = false
                }
            }
        };

        Assert.NotNull(request.BusinessId);
        Assert.Single(request.Preferences);
    }
}