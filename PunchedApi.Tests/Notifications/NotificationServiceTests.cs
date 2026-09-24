using Microsoft.EntityFrameworkCore;
using Moq;
using PunchedApi.Application.Notifications;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;
using PunchedApi.Infrastructure.Repositories;

namespace PunchedApi.Tests.Notifications;

public class NotificationServiceTests
{
    [Fact]
    public async Task Unknown_type_is_a_programmer_error()
    {
        await using var fixture = new Fixture(Array.Empty<string>());
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.SendAsync(
            new NotificationRequest("unknown.type", Guid.NewGuid(), null, new Dictionary<string, object?>())));
    }

    [Fact]
    public async Task Suppressed_intent_writes_nothing_and_returns_reason()
    {
        await using var fixture = new Fixture(Array.Empty<string>());

        var result = await fixture.Service.SendAsync(new NotificationRequest(
            "appointment.booked", Guid.NewGuid(), null, new Dictionary<string, object?>()));

        Assert.False(result.Accepted);
        Assert.Equal(NotificationService.SuppressedByPreferences, result.SuppressReason);
        Assert.Empty(await fixture.Context.Notifications.ToListAsync());
    }

    [Fact]
    public async Task Accepted_in_app_writes_inbox_and_ledger()
    {
        await using var fixture = new Fixture(new[] { NotificationChannel.InApp });

        var result = await fixture.Service.SendAsync(new NotificationRequest(
            "appointment.booked", Guid.NewGuid(), null, new Dictionary<string, object?>()));

        Assert.NotNull(result.InboxId);
        Assert.Single(await fixture.Context.Notifications.ToListAsync());
        Assert.Single(await fixture.Context.NotificationLogs.ToListAsync());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public ApplicationDbContext Context { get; }
        public NotificationService Service { get; }

        public Fixture(string[] channels)
        {
            Context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
            var preferences = new Mock<IPreferenceResolver>();
            preferences.Setup(x => x.ResolveAsync(
                    It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<string>(),
                    It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(channels);
            Service = new NotificationService(
                new UnitOfWork(Context), Context, preferences.Object,
                TestHelpers.CreateLogger<NotificationService>());
        }

        public ValueTask DisposeAsync()
        {
            Context.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}