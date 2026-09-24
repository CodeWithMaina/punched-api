using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using PunchedApi.Application.Notifications;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;
using PunchedApi.Infrastructure.Notifications;
using PunchedApi.Infrastructure.Repositories;

namespace PunchedApi.Tests.Notifications;

public class NotificationDeliveryWorkerTests
{
    [Fact]
    public async Task Queued_email_is_claimed_and_sent()
    {
        var channel = new TestNotificationChannel();
        await using var fixture = await Fixture.CreateAsync(channel);
        await fixture.AddPendingAsync();

        var claimed = await fixture.Worker.ProcessBatchAsync();

        Assert.Equal(1, claimed);
        Assert.Equal(1, channel.Calls);
        var row = await fixture.Context.NotificationLogs.SingleAsync();
        Assert.Equal("sent", row.Status);
        Assert.NotEqual(default, row.SentAt);
    }

    [Fact]
    public async Task Throwing_channel_schedules_first_retry()
    {
        var channel = new TestNotificationChannel { ThrowOnSend = true };
        await using var fixture = await Fixture.CreateAsync(channel);
        await fixture.AddPendingAsync();
        var before = DateTime.UtcNow;

        await fixture.Worker.ProcessBatchAsync();

        var row = await fixture.Context.NotificationLogs.AsNoTracking().SingleAsync();
        Assert.Equal("pending", row.Status);
        Assert.Equal(1, row.Attempts);
        Assert.True(row.NextAttemptAt >= before.AddSeconds(29));
    }

    [Fact]
    public async Task Third_attempt_failure_is_terminal_and_truncated()
    {
        var channel = new TestNotificationChannel { ThrowOnSend = true };
        await using var fixture = await Fixture.CreateAsync(channel);
        await fixture.AddPendingAsync(attempts: 2);

        await fixture.Worker.ProcessBatchAsync();

        var row = await fixture.Context.NotificationLogs.AsNoTracking().SingleAsync();
        Assert.Equal("failed", row.Status);
        Assert.Equal(3, row.Attempts);
        Assert.Equal(500, row.Error!.Length);
    }

    [Fact]
    public async Task Unknown_channel_fails_and_worker_can_run_again()
    {
        await using var fixture = await Fixture.CreateAsync(new TestNotificationChannel { Name = "sms" });
        await fixture.AddPendingAsync(channel: "email");

        await fixture.Worker.ProcessBatchAsync();
        var failed = await fixture.Context.NotificationLogs.AsNoTracking().SingleAsync();
        Assert.Equal("failed", failed.Status);
        Assert.Equal("no_channel_registered", failed.Error);

        await fixture.Worker.ProcessBatchAsync();
        Assert.Equal(1, await fixture.Context.NotificationLogs.CountAsync());
    }

    [Fact]
    public async Task Poison_row_does_not_block_good_row()
    {
        var poisonId = Guid.NewGuid();
        var channel = new TestNotificationChannel { ThrowForOutboxId = poisonId };
        await using var fixture = await Fixture.CreateAsync(channel);
        await fixture.AddPendingAsync("{\"businessName\":\"Cafe\",\"scheduledAt\":\"Tomorrow\"}", createdAt: DateTime.UtcNow.AddMinutes(-1));
        await fixture.AddPendingAsync("{\"businessName\":\"Cafe\",\"scheduledAt\":\"Tomorrow\"}", attempts: 2, createdAt: DateTime.UtcNow, id: poisonId);

        await fixture.Worker.ProcessBatchAsync();

        Assert.Equal(2, channel.Calls);
        Assert.Equal(1, await fixture.Context.NotificationLogs.CountAsync(row => row.Status == "failed"));
        Assert.Equal(1, await fixture.Context.NotificationLogs.CountAsync(row => row.Status == "sent"));
    }

    [Fact]
    public async Task Same_idempotency_key_queues_exactly_one_row()
    {
        await using var fixture = await Fixture.CreateAsync(new TestNotificationChannel());
        var service = fixture.CreateNotificationService("email");
        var request = new NotificationRequest(
            "appointment.booked", Guid.NewGuid(), null,
            new Dictionary<string, object?> { ["businessName"] = "Cafe" }, "appointment:one");

        await service.SendAsync(request);
        await service.SendAsync(request with { Data = new Dictionary<string, object?> { ["businessName"] = "Other" } });

        var rows = await fixture.Context.NotificationLogs.Where(row => row.IdempotencyKey == "appointment:one").ToListAsync();
        Assert.Single(rows);
        Assert.Equal("pending", rows[0].Status);
    }

    [Fact]
    public async Task In_app_stays_inline_and_never_enters_outbox()
    {
        await using var fixture = await Fixture.CreateAsync(new TestNotificationChannel());
        var service = fixture.CreateNotificationService("in_app");

        var result = await service.SendAsync(new NotificationRequest(
            "appointment.booked", Guid.NewGuid(), null,
            new Dictionary<string, object?> { ["businessName"] = "Cafe" }));

        Assert.True(result.Accepted);
        Assert.NotNull(result.InboxId);
        Assert.Equal(1, await fixture.Context.Notifications.CountAsync());
        Assert.Equal(1, await fixture.Context.NotificationLogs.CountAsync(row => row.Channel == "in_app" && row.Status == "sent"));
        Assert.Equal(0, await fixture.Context.NotificationLogs.CountAsync(row => row.Status == "pending"));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        public ApplicationDbContext Context { get; }
        public NotificationDeliveryWorker Worker { get; }

        private Fixture(ServiceProvider provider, ApplicationDbContext context, NotificationDeliveryWorker worker)
        {
            _provider = provider;
            Context = context;
            Worker = worker;
        }

        public static async Task<Fixture> CreateAsync(TestNotificationChannel channel)
        {
            var database = Guid.NewGuid().ToString("N");
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<ApplicationDbContext>(options => options.UseInMemoryDatabase(database));
            services.AddScoped<NotificationOutboxStore>();
            services.AddSingleton<INotificationChannel>(channel);
            var provider = services.BuildServiceProvider();
            var context = provider.GetRequiredService<ApplicationDbContext>();
            await context.Database.EnsureCreatedAsync();
            var worker = new NotificationDeliveryWorker(
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<ILogger<NotificationDeliveryWorker>>());
            return new Fixture(provider, context, worker);
        }

        public async Task AddPendingAsync(
            string payload = "{}",
            int attempts = 0,
            string channel = NotificationChannel.Email,
            DateTime? createdAt = null,
            Guid? id = null)
        {
            Context.NotificationLogs.Add(new NotificationLog
            {
                Id = id ?? Guid.NewGuid(), UserId = Guid.NewGuid(), Channel = channel,
                TemplateType = "appointment.booked", Status = "pending", PayloadJson = payload,
                Attempts = attempts, NextAttemptAt = DateTime.UtcNow.AddMinutes(-1),
                IdempotencyKey = Guid.NewGuid().ToString("N"), UpdatedAt = DateTime.UtcNow,
                CreatedAt = createdAt ?? DateTime.UtcNow
            });
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
        }

        public NotificationService CreateNotificationService(params string[] channels)
        {
            var preferences = new Mock<IPreferenceResolver>();
            preferences.Setup(x => x.ResolveAsync(
                    It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<string>(),
                    It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(channels);
            return new NotificationService(
                new UnitOfWork(Context), Context, preferences.Object,
                TestHelpers.CreateLogger<NotificationService>());
        }

        public async ValueTask DisposeAsync()
        {
            Context.ChangeTracker.Clear();
            await _provider.DisposeAsync();
        }
    }
}
