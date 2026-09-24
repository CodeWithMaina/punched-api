using PunchedApi.Application.Notifications;

namespace PunchedApi.Tests.Notifications;

/// <summary>Configurable channel used only by the delivery-worker tests.</summary>
public sealed class TestNotificationChannel : INotificationChannel
{
    public string Name { get; init; } = NotificationChannel.Email;
    public bool ThrowOnSend { get; set; }
    public Guid? ThrowForOutboxId { get; set; }
    public int Calls { get; private set; }
    public List<ChannelMessage> Messages { get; } = new();

    public Task SendAsync(ChannelMessage message, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Calls++;
        if (ThrowOnSend || ThrowForOutboxId == message.OutboxId)
            throw new InvalidOperationException(new string('x', 600));
        Messages.Add(message);
        return Task.CompletedTask;
    }
}