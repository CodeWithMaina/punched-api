using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Tests;

public class NotificationProducerParityTests
{
    [Fact]
    public async Task Stamp_award_reaching_staff_daily_goal_writes_canonical_inbox_row()
    {
        using var harness = new StampingEcosystemTests();
        var tenant = await harness.SeedTenantAsync();
        tenant.Staff.DailyGoalOverride = 1;
        await harness.Context.SaveChangesAsync();
        const string token = "notification-goal-token";
        await harness.SeedTokenAsync(tenant, token);

        var service = harness.CreateStampService(tenant);
        var result = await service.AwardStampAsync(tenant.Staff.Id, new AwardStampRequest
        {
            Token = token,
            BusinessId = tenant.Business.Id
        });

        Assert.True(result.Success, result.Error?.Message);
        var notification = await harness.Context.Notifications.SingleAsync(row =>
            row.UserId == tenant.Staff.Id && row.Type == "loyalty.goal_reached");
        Assert.Equal(tenant.Business.Id, notification.BusinessId);
        Assert.Equal(1, notification.StampsCount);
        Assert.Contains("businessName", notification.PayloadJson);
    }

    [Fact]
    public async Task Customer_reschedule_request_writes_canonical_inbox_row()
    {
        using var connection = BookingTestBase.CreateConnection();
        await using var context = BookingTestBase.CreateContext(connection);
        var owner = BookingTestBase.CreateOwner("notify-owner@test.com");
        var business = BookingTestBase.CreateBusiness(owner.Id);
        var customer = BookingTestBase.CreateCustomer("notify-customer@test.com");
        var staff = BookingTestBase.CreateStaff(business.Id, "notify-staff@test.com");
        var serviceItem = BookingTestBase.CreateService(business.Id, "Consultation", 60, 500m);
        var scheduledAt = DateTime.UtcNow.Date.AddDays(14).AddHours(10);
        var proposedAt = scheduledAt.AddDays(1).AddHours(2);
        var appointment = BookingTestBase.CreateAppointment(
            business.Id, customer.Id, staff.Id, scheduledAt, scheduledAt.AddHours(1));

        await BookingTestBase.SeedAsync(context,
            owner, business, customer, staff, serviceItem,
            BookingTestBase.CreateAssignment(business.Id, staff.Id, serviceItem.Id),
            BookingTestBase.CreateShift(business.Id, staff.Id, DateOnly.FromDateTime(scheduledAt), 9, 18),
            BookingTestBase.CreateShift(business.Id, staff.Id, DateOnly.FromDateTime(proposedAt), 9, 18),
            appointment,
            BookingTestBase.CreateResource(appointment.Id, serviceItem.Id, serviceItem.Name, 60, 500m, 0));

        var service = BookingTestBase.CreateAppointmentService(context);
        var result = await service.RescheduleAsync(customer.Id, "Customer", appointment.Id,
            new RescheduleAppointmentRequest { ScheduledAt = proposedAt });

        Assert.True(result.Success, result.Error?.Message);
        var notification = await context.Notifications.SingleAsync(row =>
            row.UserId == owner.Id && row.Type == "appointment.reschedule_requested");
        Assert.Equal(business.Id, notification.BusinessId);
        Assert.Equal(appointment.Id, notification.AppointmentId);
    }

    [Fact]
    public async Task Redemption_fulfilment_writes_canonical_inbox_row()
    {
        using var harness = new StampingEcosystemTests();
        var tenant = await harness.SeedTenantAsync(totalStamps: 5, lifetimeStamps: 5);
        var service = harness.CreateRedemptionService(tenant);
        var claim = await service.ClaimRewardAsync(tenant.Customer.Id,
            new ClaimRewardRequest { CardId = tenant.Card.Id });
        Assert.True(claim.Success, claim.Error?.Message);

        harness.Context.ChangeTracker.Clear();
        var result = await service.FulfillRedemptionAsync(tenant.Staff.Id, new FulfillRedemptionRequest
        {
            CardId = tenant.Card.Id,
            BusinessId = tenant.Business.Id,
            Code = claim.Data!.FulfilmentCode!
        });

        Assert.True(result.Success, result.Error?.Message);
        var notification = await harness.Context.Notifications.SingleAsync(row =>
            row.UserId == tenant.Customer.Id && row.Type == "loyalty.reward_fulfilled");
        Assert.Equal(tenant.Business.Id, notification.BusinessId);
        Assert.Contains("rewardName", notification.PayloadJson);
    }

    [Fact]
    public async Task Card_correction_preserves_legacy_event_as_canonical_notification()
    {
        using var harness = new StampingEcosystemTests();
        var tenant = await harness.SeedTenantAsync(totalStamps: 2, lifetimeStamps: 2);

        var result = await harness.CreateStampService(tenant).AdjustStampsAsync(
            tenant.Owner.Id,
            new StampAdjustmentRequest
            {
                CardId = tenant.Card.Id,
                Delta = 1,
                Reason = StampAdjustmentReason.ManualCorrection,
                Note = "correction"
            });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Single(await harness.Context.Notifications.Where(row =>
            row.UserId == tenant.Customer.Id && row.Type == "loyalty.card_corrected").ToListAsync());
    }
}