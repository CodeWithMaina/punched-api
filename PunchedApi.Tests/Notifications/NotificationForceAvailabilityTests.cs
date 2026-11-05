using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Moq;
using PunchedApi.Application.Notifications;
using PunchedApi.Application.Settings;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;
using PunchedApi.Infrastructure.Repositories;

namespace PunchedApi.Tests.Notifications;

/// <summary>
/// The SECURITY Force path bypasses <em>preferences</em> (an OTP must not be
/// silenced) but must still respect <em>availability</em>: queuing a channel the
/// deployment cannot deliver on only dead-letters it.
/// </summary>
public class NotificationForceAvailabilityTests
{
    [Fact]
    public async Task Security_force_queues_only_available_channels()
    {
        await using var context = CreateContext();
        var availability = new Mock<INotificationChannelAvailability>();
        availability
            .Setup(a => a.FilterAvailableAsync(
                It.IsAny<Guid>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { NotificationChannel.Email });

        var service = CreateService(context, availability.Object);

        var result = await service.SendAsync(new NotificationRequest(
            "security.verification_code",
            Guid.NewGuid(),
            null,
            new Dictionary<string, object?> { ["code"] = "123456" },
            Force: true));

        Assert.True(result.Accepted);
        Assert.Equal(new[] { NotificationChannel.Email }, result.Channels);
        // No in-app row: the code must not be persisted into an inbox payload.
        Assert.Empty(await context.Notifications.ToListAsync());
        Assert.Single(await context.NotificationLogs.Where(log => log.Channel == NotificationChannel.Email).ToListAsync());
    }

    [Fact]
    public async Task Security_force_with_no_available_channel_is_suppressed()
    {
        await using var context = CreateContext();
        var availability = new Mock<INotificationChannelAvailability>();
        availability
            .Setup(a => a.FilterAvailableAsync(
                It.IsAny<Guid>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        var service = CreateService(context, availability.Object);

        var result = await service.SendAsync(new NotificationRequest(
            "security.verification_code",
            Guid.NewGuid(),
            null,
            new Dictionary<string, object?> { ["code"] = "123456" },
            Force: true));

        Assert.False(result.Accepted);
        Assert.Empty(await context.NotificationLogs.ToListAsync());
    }

    [Fact]
    public async Task Non_security_intents_still_use_preferences()
    {
        await using var context = CreateContext();
        var preferences = new Mock<IPreferenceResolver>();
        preferences
            .Setup(p => p.ResolveAsync(
                It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<string>(),
                It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { NotificationChannel.InApp });

        // Availability must NOT be consulted for a normal (non-forced) intent.
        var availability = new Mock<INotificationChannelAvailability>(MockBehavior.Strict);
        var service = CreateService(context, availability.Object, preferences.Object);

        var result = await service.SendAsync(new NotificationRequest(
            "appointment.booked",
            Guid.NewGuid(),
            null,
            new Dictionary<string, object?> { ["businessName"] = "Cafe", ["scheduledAt"] = "tomorrow" }));

        Assert.True(result.Accepted);
        Assert.Equal(new[] { NotificationChannel.InApp }, result.Channels);
        availability.VerifyNoOtherCalls();
    }

    private static ApplicationDbContext CreateContext() => new(
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private static NotificationService CreateService(
        ApplicationDbContext context,
        INotificationChannelAvailability availability,
        IPreferenceResolver? preferences = null) => new(
        new UnitOfWork(context),
        context,
        preferences ?? new Mock<IPreferenceResolver>().Object,
        TestHelpers.CreateLogger<NotificationService>(),
        null,
        availability);
}
