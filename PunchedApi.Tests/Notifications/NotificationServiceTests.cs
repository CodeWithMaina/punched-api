using Microsoft.EntityFrameworkCore;
using Moq;
using PunchedApi.Application.Notifications;
using PunchedApi.Domain.Entities;
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

    [Fact]
    public async Task Staff_member_cannot_be_notified_in_another_business_context()
    {
        await using var fixture = new Fixture(new[] { NotificationChannel.InApp });
        var firstBusinessId = Guid.NewGuid();
        var secondBusinessId = Guid.NewGuid();
        var firstOwnerId = Guid.NewGuid();
        var secondOwnerId = Guid.NewGuid();
        var staffId = Guid.NewGuid();

        fixture.Context.Users.AddRange(
            new User { Id = firstOwnerId, Email = "owner-a@test.com", FullName = "Owner A", Role = UserRole.Business },
            new User { Id = secondOwnerId, Email = "owner-b@test.com", FullName = "Owner B", Role = UserRole.Business },
            new User
            {
                Id = staffId,
                Email = "staff-a@test.com",
                FullName = "Staff A",
                Role = UserRole.Staff,
                StaffBusinessId = firstBusinessId
            });
        fixture.Context.Businesses.AddRange(
            new Business
            {
                Id = firstBusinessId,
                OwnerId = firstOwnerId,
                Name = "Business A",
                Category = "Cafe",
                Location = "Nairobi",
                MpesaNumber = "254700000001"
            },
            new Business
            {
                Id = secondBusinessId,
                OwnerId = secondOwnerId,
                Name = "Business B",
                Category = "Cafe",
                Location = "Nairobi",
                MpesaNumber = "254700000002"
            });
        await fixture.Context.SaveChangesAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.SendAsync(new NotificationRequest(
            "loyalty.goal_reached",
            staffId,
            secondBusinessId,
            new Dictionary<string, object?> { ["businessName"] = "Business B" })));

        Assert.Empty(await fixture.Context.Notifications.ToListAsync());
        Assert.Empty(await fixture.Context.NotificationLogs.ToListAsync());
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