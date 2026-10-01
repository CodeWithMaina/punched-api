using MimeKit;

namespace PunchedApi.Infrastructure.Notifications;

public interface INotificationSmtpDelivery
{
    Task SendAsync(MimeMessage message, CancellationToken cancellationToken);
}