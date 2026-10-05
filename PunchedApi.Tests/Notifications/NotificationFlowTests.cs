using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;
using PunchedApi.Infrastructure.Repositories;

namespace PunchedApi.Tests.Notifications;

public class NotificationFlowTests
{
    [Fact]
    public async Task Tenant_isolation_is_enforced_by_the_service_query()
    {
        await using var fixture = new Fixture();
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        fixture.Context.Notifications.AddRange(
            new Notification { Id = Guid.NewGuid(), UserId = owner, Type = "GoalReached" },
            new Notification { Id = Guid.NewGuid(), UserId = other, Type = "GoalReached" });
        await fixture.Context.SaveChangesAsync();

        var visible = await fixture.Service.GetAsync(owner, unreadOnly: false);

        Assert.Single(visible);
        Assert.Single(await fixture.Context.Notifications.Where(row => row.UserId == owner).ToListAsync());
    }

    [Fact]
    public async Task Get_includes_valid_payload_and_returns_empty_object_for_malformed_payload()
    {
        await using var fixture = new Fixture();
        var userId = Guid.NewGuid();
        fixture.Context.Notifications.AddRange(
            new Notification
            {
                Id = Guid.NewGuid(), UserId = userId, Type = "appointment.booked",
                PayloadJson = "{\"businessName\":\"Cafe\"}", CreatedAt = DateTime.UtcNow
            },
            new Notification
            {
                Id = Guid.NewGuid(), UserId = userId, Type = "appointment.booked",
                PayloadJson = "{invalid", CreatedAt = DateTime.UtcNow.AddSeconds(-1)
            });
        await fixture.Context.SaveChangesAsync();

        var notifications = await fixture.Service.GetAsync(userId, unreadOnly: false);

        Assert.Equal(2, notifications.Count);
        var validPayload = Assert.IsType<System.Text.Json.JsonElement>(notifications[0].Payload["businessName"]);
        Assert.Equal("Cafe", validPayload.GetString());
        Assert.Empty(notifications[1].Payload);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public ApplicationDbContext Context { get; }
        public NotificationsService Service { get; }

        public Fixture()
        {
            Context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
            Service = new NotificationsService(
                new UnitOfWork(Context), Context, TestHelpers.CreateLogger<NotificationsService>());
        }

        public ValueTask DisposeAsync()
        {
            Context.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}