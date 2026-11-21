using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.BackgroundProcessing;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Tests.Database;

/// <summary>
/// The publish half of the wake-up path: a unit of work that commits durable
/// background work must raise exactly one hint, and a unit of work that commits
/// ordinary rows must raise none. Delivery to another process is covered separately
/// (a Redis publish from one process wakes a worker in another).
/// </summary>
public class OutboxWakeInterceptorTests
{
    private sealed class RecordingWakeChannel : IPollWakeChannel
    {
        public int Notifications { get; private set; }

        public void Notify(string reason) => Notifications++;

        public Task<bool> WaitForWakeAsync(TimeSpan timeout, CancellationToken ct) =>
            Task.FromResult(false);
    }

    private static ApplicationDbContext Context(IPollWakeChannel channel, string name) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(name)
            .AddInterceptors(new OutboxWakeInterceptor(channel))
            .Options);

    [Fact]
    public async Task Committing_a_notification_outbox_row_raises_one_hint()
    {
        var channel = new RecordingWakeChannel();
        await using var db = Context(channel, Guid.NewGuid().ToString("N"));

        db.NotificationLogs.Add(new NotificationLog
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            Channel = "email",
            TemplateType = "appointment.booked",
            Status = "pending",
            PayloadJson = "{}",
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            NextAttemptAt = DateTime.UtcNow
        });

        await db.SaveChangesAsync();

        Assert.Equal(1, channel.Notifications);
    }

    [Fact]
    public async Task Committing_only_ordinary_rows_raises_no_hint()
    {
        var channel = new RecordingWakeChannel();
        await using var db = Context(channel, Guid.NewGuid().ToString("N"));

        db.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            Email = $"{Guid.NewGuid():N}@example.test",
            FullName = "No Wake",
            PhoneNumber = "0700000001",
            CreatedAt = DateTime.UtcNow
        });

        await db.SaveChangesAsync();

        Assert.Equal(0, channel.Notifications);
    }

    [Fact]
    public async Task A_second_commit_in_the_same_context_raises_its_own_hint()
    {
        var channel = new RecordingWakeChannel();
        await using var db = Context(channel, Guid.NewGuid().ToString("N"));

        db.DomainEvents.Add(new DomainEvent
        {
            Id = Guid.NewGuid(),
            EventType = "appointment.created",
            AggregateType = "appointment",
            AggregateId = Guid.NewGuid(),
            PayloadJson = "{}",
            Status = "pending",
            OccurredAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        db.DomainEvents.Add(new DomainEvent
        {
            Id = Guid.NewGuid(),
            EventType = "appointment.cancelled",
            AggregateType = "appointment",
            AggregateId = Guid.NewGuid(),
            PayloadJson = "{}",
            Status = "pending",
            OccurredAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        Assert.Equal(2, channel.Notifications);
    }
}
