using System.Collections.Concurrent;
using PunchedApi.Application.Notifications;

namespace PunchedApi.Tests.Notifications;

/// <summary>Configurable channel used only by the delivery-worker tests.</summary>
public sealed class TestNotificationChannel : INotificationChannel
{
    public string Name { get; init; } = NotificationChannel.Email;
    public bool ThrowOnSend { get; set; }
    public bool PermanentFailure { get; set; }
    public Guid? ThrowForOutboxId { get; set; }
    private int _calls;
    public int Calls => Volatile.Read(ref _calls);
    public ConcurrentQueue<ChannelMessage> Messages { get; } = new();

    public Task SendAsync(ChannelMessage message, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _calls);
        if (PermanentFailure)
            throw new PermanentChannelException("Permanent test failure.");
        if (ThrowOnSend || ThrowForOutboxId == message.OutboxId)
            throw new InvalidOperationException(new string('x', 600));
        Messages.Enqueue(message);
        return Task.CompletedTask;
    }
}