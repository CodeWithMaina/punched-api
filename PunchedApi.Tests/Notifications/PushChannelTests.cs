using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PunchedApi.Application.Notifications;
using PunchedApi.Application.Settings;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;
using PunchedApi.Infrastructure.Notifications;
using WebPush;

namespace PunchedApi.Tests.Notifications;

/// <summary>
/// Hardening contract of the web-push channel: provider configuration is required,
/// a missing device is a successful no-op, a dead subscription is pruned without
/// failing the whole row, a partial success is never retried (which would
/// re-notify devices already reached), and a total failure is retryable.
/// </summary>
public class PushChannelTests
{
    [Fact]
    public async Task Unconfigured_vapid_fails_permanently()
    {
        await using var context = CreateContext();
        var channel = CreateChannel(context, new RecordingPushSender(), enabled: false);

        await Assert.ThrowsAsync<PermanentChannelException>(
            () => channel.SendAsync(Message(Guid.NewGuid())));
    }

    [Fact]
    public async Task No_registered_device_is_a_successful_noop()
    {
        await using var context = CreateContext();
        var sender = new RecordingPushSender();
        var channel = CreateChannel(context, sender);

        await channel.SendAsync(Message(Guid.NewGuid()));

        Assert.Empty(sender.Sends);
    }

    [Fact]
    public async Task Partial_failure_delivers_to_the_rest_and_prunes_only_the_dead_device()
    {
        await using var context = CreateContext();
        var userId = Guid.NewGuid();
        var live = CreateDevice(userId, "https://push.example/live");
        var dead = CreateDevice(userId, "https://push.example/dead");
        context.PushDevices.AddRange(live, dead);
        await context.SaveChangesAsync();

        var sender = new RecordingPushSender();
        sender.FailWith("https://push.example/dead", PushFailure(HttpStatusCode.Gone));
        var channel = CreateChannel(context, sender);

        // One delivered + one pruned => success. No throw, so the worker never
        // retries and re-notifies the device that already received the message.
        await channel.SendAsync(Message(userId));

        Assert.Equal(2, sender.Sends.Count);
        Assert.True(live.IsActive);
        Assert.False(dead.IsActive);
    }

    [Fact]
    public async Task Every_device_failing_is_retryable()
    {
        await using var context = CreateContext();
        var userId = Guid.NewGuid();
        context.PushDevices.AddRange(
            CreateDevice(userId, "https://push.example/a"),
            CreateDevice(userId, "https://push.example/b"));
        await context.SaveChangesAsync();

        var sender = new RecordingPushSender();
        sender.FailWith("https://push.example/a", PushFailure(HttpStatusCode.ServiceUnavailable));
        sender.FailWith("https://push.example/b", PushFailure(HttpStatusCode.ServiceUnavailable));
        var channel = CreateChannel(context, sender);

        await Assert.ThrowsAsync<RetryableChannelException>(() => channel.SendAsync(Message(userId)));
    }

    [Fact]
    public async Task Payload_coalesces_per_appointment_and_deep_links_to_it()
    {
        await using var context = CreateContext();
        var userId = Guid.NewGuid();
        context.PushDevices.Add(CreateDevice(userId, "https://push.example/one"));
        await context.SaveChangesAsync();
        var appointmentId = Guid.NewGuid();
        var sender = new RecordingPushSender();
        var channel = CreateChannel(context, sender);

        await channel.SendAsync(Message(userId, new Dictionary<string, object?>
        {
            ["renderedTitle"] = "Reminder",
            ["renderedBody"] = "See you soon",
            ["appointmentId"] = appointmentId
        }));

        using var payload = JsonDocument.Parse(sender.Sends.Single().Payload);
        Assert.Equal($"appointment-{appointmentId}", payload.RootElement.GetProperty("tag").GetString());
        Assert.Equal(
            $"/dashboard/appointments/{appointmentId}",
            payload.RootElement.GetProperty("data").GetProperty("url").GetString());
    }

    [Fact]
    public async Task Payload_without_appointment_tags_by_type()
    {
        await using var context = CreateContext();
        var userId = Guid.NewGuid();
        context.PushDevices.Add(CreateDevice(userId, "https://push.example/one"));
        await context.SaveChangesAsync();
        var sender = new RecordingPushSender();
        var channel = CreateChannel(context, sender);

        await channel.SendAsync(Message(userId));

        using var payload = JsonDocument.Parse(sender.Sends.Single().Payload);
        Assert.Equal("punched-appointment.booked", payload.RootElement.GetProperty("tag").GetString());
        Assert.Equal("/dashboard/notifications", payload.RootElement.GetProperty("data").GetProperty("url").GetString());
    }

    private static ApplicationDbContext CreateContext() => new(
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    /// <summary>
    /// WebPushException derives its <c>StatusCode</c> from the response it carries,
    /// so a provider rejection is simulated with a matching HttpResponseMessage.
    /// </summary>
    private static WebPushException PushFailure(HttpStatusCode statusCode) => new(
        "push rejected",
        new PushSubscription("https://push.example/subscription", "p256dh", "auth"),
        new HttpResponseMessage(statusCode));

    private static PushDevice CreateDevice(Guid userId, string endpoint) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        Endpoint = endpoint,
        P256dh = "p256dh",
        Auth = "auth",
        IsActive = true,
        LastSeenAt = DateTime.UtcNow
    };

    private static PushChannel CreateChannel(
        ApplicationDbContext context, IPushNotificationSender sender, bool enabled = true) => new(
        context,
        Options.Create(new VapidSettings
        {
            Enabled = enabled,
            PublicKey = enabled ? "test-public-key" : string.Empty,
            PrivateKey = enabled ? "test-private-key" : string.Empty,
            Subject = "mailto:test@example.com"
        }),
        sender,
        TestHelpers.CreateLogger<PushChannel>());

    private static ChannelMessage Message(
        Guid recipientId, IReadOnlyDictionary<string, object?>? data = null) => new(
        Guid.NewGuid(),
        "appointment.booked",
        recipientId,
        null,
        NotificationCategory.Appointment,
        data ?? new Dictionary<string, object?>
        {
            ["renderedTitle"] = "Appointment booked",
            ["renderedBody"] = "Your appointment is booked."
        });

    private sealed class RecordingPushSender : IPushNotificationSender
    {
        private readonly Dictionary<string, Exception> _failures = new(StringComparer.Ordinal);

        public List<(PushSubscription Subscription, string Payload)> Sends { get; } = new();

        public void FailWith(string endpoint, Exception exception) => _failures[endpoint] = exception;

        public Task SendAsync(
            PushSubscription subscription, string payload, VapidDetails vapidDetails, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Sends.Add((subscription, payload));
            if (_failures.TryGetValue(subscription.Endpoint, out var failure)) throw failure;
            return Task.CompletedTask;
        }
    }
}
