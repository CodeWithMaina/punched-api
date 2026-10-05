using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Notifications;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;
using PunchedApi.Infrastructure.Repositories;

namespace PunchedApi.Tests.Notifications;

public class NotificationPreferenceEnforcementTests
{
    [Fact]
    public async Task Loyalty_email_opt_out_suppresses_the_stamp_outbox_row()
    {
        using var harness = new StampingEcosystemTests();
        var tenant = await PrepareGoalStampAsync(harness);
        var preferences = harness.CreatePreferenceResolver();
        await preferences.UpsertAsync(tenant.Staff.Id, tenant.Business.Id,
            new[] { LoyaltyEmail(enabled: false) });

        var result = await AwardGoalStampAsync(harness, tenant);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Empty(await harness.Context.NotificationLogs.Where(row =>
            row.UserId == tenant.Staff.Id && row.Channel == NotificationChannel.Email).ToListAsync());
        Assert.Single(await harness.Context.Notifications.Where(row =>
            row.UserId == tenant.Staff.Id && row.Type == "loyalty.goal_reached").ToListAsync());
    }

    [Fact]
    public async Task Loyalty_email_default_enabled_queues_exactly_one_pending_row()
    {
        using var harness = new StampingEcosystemTests();
        var tenant = await PrepareGoalStampAsync(harness);
        var preferences = harness.CreatePreferenceResolver();
        await preferences.UpsertAsync(tenant.Staff.Id, tenant.Business.Id,
            new[] { LoyaltyEmail(enabled: false) });
        await preferences.UpsertAsync(tenant.Staff.Id, tenant.Business.Id,
            new[] { LoyaltyEmail(enabled: true) });

        Assert.Empty(await harness.Context.NotificationPreferences.Where(row =>
            row.UserId == tenant.Staff.Id && row.BusinessId == tenant.Business.Id).ToListAsync());

        var result = await AwardGoalStampAsync(harness, tenant);

        Assert.True(result.Success, result.Error?.Message);
        var emailRows = await harness.Context.NotificationLogs.Where(row =>
            row.UserId == tenant.Staff.Id && row.Channel == NotificationChannel.Email &&
            row.TemplateType == "loyalty.goal_reached").ToListAsync();
        var emailRow = Assert.Single(emailRows);
        Assert.Equal("pending", emailRow.Status);
        Assert.Equal($"stamp:{await GetAwardedStampIdAsync(harness, tenant)}:goal-reached", emailRow.IdempotencyKey);
    }

    [Fact]
    public async Task Marketing_sms_resolves_default_off()
    {
        await using var context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        var unitOfWork = new UnitOfWork(context);
        var resolver = new PreferenceResolver(
            unitOfWork,
            new MemoryCache(new MemoryCacheOptions()),
            Array.Empty<INotificationChannel>());

        var channels = await resolver.ResolveAsync(
            Guid.NewGuid(), null, NotificationCategory.Marketing, new[] { NotificationChannel.Sms });

        Assert.Empty(channels);
        Assert.False(NotificationDefaults.IsEnabled(NotificationCategory.Marketing, NotificationChannel.Sms));
    }

    [Fact]
    public async Task Notification_exception_does_not_fail_committed_stamp_award()
    {
        using var harness = new StampingEcosystemTests();
        var tenant = await PrepareGoalStampAsync(harness);
        var notificationService = new Mock<INotificationService>();
        notificationService
            .Setup(service => service.SendAsync(It.IsAny<NotificationRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("notification storage unavailable"));

        var result = await AwardGoalStampAsync(harness, tenant, notificationService.Object);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Single(await harness.Context.Stamps.Where(stamp => stamp.CardId == tenant.Card.Id).ToListAsync());
        notificationService.Verify(service => service.SendAsync(
            It.Is<NotificationRequest>(request => request.Type == "loyalty.goal_reached"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private static NotificationPreferenceItem LoyaltyEmail(bool enabled) => new()
    {
        Category = NotificationCategory.Loyalty,
        Channel = NotificationChannel.Email,
        Enabled = enabled
    };

    private static async Task<StampingEcosystemTests.Tenant> PrepareGoalStampAsync(StampingEcosystemTests harness)
    {
        var tenant = await harness.SeedTenantAsync();
        tenant.Staff.DailyGoalOverride = 1;
        await harness.Context.SaveChangesAsync();
        await harness.SeedTokenAsync(tenant, "preference-test-token");
        return tenant;
    }

    private static Task<PunchedApi.Application.DTOs.ApiResponse<PunchedApi.Application.DTOs.StampAwardedResponse>>
        AwardGoalStampAsync(
            StampingEcosystemTests harness,
            StampingEcosystemTests.Tenant tenant,
            INotificationService? notificationService = null) =>
        harness.CreateStampService(tenant, notificationService).AwardStampAsync(
            tenant.Staff.Id,
            new PunchedApi.Application.DTOs.AwardStampRequest
            {
                Token = "preference-test-token",
                BusinessId = tenant.Business.Id
            });

    private static async Task<Guid> GetAwardedStampIdAsync(
        StampingEcosystemTests harness,
        StampingEcosystemTests.Tenant tenant) =>
        (await harness.Context.Stamps.SingleAsync(stamp => stamp.CardId == tenant.Card.Id)).Id;
}