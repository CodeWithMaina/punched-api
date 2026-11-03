using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Notifications;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Repositories;

namespace PunchedApi.Tests;

/// <summary>
/// The storefront enquiry notification.
///
/// <para>The visitor's enquiry is an in-app row for the owner, always. Email is
/// an addition to that, never a replacement: the in-app row is written first, so
/// a delivery fault (an unconfigured sender, a dead SMTP host) cannot lose the
/// enquiry — which is what the owner would actually notice.</para>
/// </summary>
public sealed class StorefrontEnquiryNotificationTests
{
    private static CreateStorefrontEnquiryRequest Request() => new()
    {
        Topic = "services",
        Answers = new List<string> { "Ask about our services", "Prices" },
        Name = "Peter Chege",
        PhoneNumber = "0712 345 678",
        Message = "Do you do balayage?"
    };

    private static StorefrontEnquiryService Service(
        Infrastructure.Data.ApplicationDbContext context,
        INotificationService notifications) =>
        new(new UnitOfWork(context), notifications, TestHelpers.CreateLogger<StorefrontEnquiryService>());

    [Fact]
    public async Task An_enquiry_writes_an_in_app_row_and_an_email_outbox_row()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var owner = BookingTestBase.CreateOwner();
        var business = BookingTestBase.CreateBusiness(owner.Id);
        await BookingTestBase.SeedAsync(context, owner, business);

        var service = Service(context, BookingTestBase.CreateNotificationService(context));
        var result = await service.SubmitAsync(business.Id, Request());
        Assert.True(result.Success, result.Error?.Message);

        // In-app: the owner reads it in their console.
        var inbox = await context.Notifications.AsNoTracking()
            .SingleAsync(row => row.UserId == owner.Id && row.BusinessId == business.Id);
        Assert.Contains(result.Data!.Reference, inbox.PayloadJson);

        // Email: queued on the outbox for the worker, with the enquiry's own
        // idempotency key so a retry cannot double-send.
        var email = await context.NotificationLogs.AsNoTracking()
            .SingleAsync(row => row.UserId == owner.Id && row.Channel == "email");
        Assert.Equal("storefront.enquiry", email.TemplateType);
        Assert.Equal($"storefront-enquiry:{business.Id}:{result.Data.Reference}", email.IdempotencyKey);
        Assert.Equal("pending", email.Status);
    }

    [Fact]
    public async Task The_queued_email_carries_what_the_owner_needs_to_reply()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var owner = BookingTestBase.CreateOwner();
        var business = BookingTestBase.CreateBusiness(owner.Id);
        await BookingTestBase.SeedAsync(context, owner, business);

        var service = Service(context, BookingTestBase.CreateNotificationService(context));
        var result = await service.SubmitAsync(business.Id, Request());

        var email = await context.NotificationLogs.AsNoTracking()
            .SingleAsync(row => row.Channel == "email");

        // The template tokens are satisfied, and the reply path is present.
        Assert.Contains("Peter Chege", email.PayloadJson);
        Assert.Contains(result.Data!.Reference, email.PayloadJson);
        Assert.Contains("254712345678", email.PayloadJson);
    }

    [Fact]
    public async Task A_repeated_intent_with_the_same_key_queues_exactly_one_email()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var owner = BookingTestBase.CreateOwner();
        var business = BookingTestBase.CreateBusiness(owner.Id);
        await BookingTestBase.SeedAsync(context, owner, business);

        var notifications = BookingTestBase.CreateNotificationService(context);
        var intent = new NotificationRequest(
            Type: "storefront.enquiry",
            RecipientUserId: owner.Id,
            BusinessId: business.Id,
            Data: new Dictionary<string, object?>
            {
                ["businessName"] = business.Name,
                ["topic"] = "your services",
                ["visitorName"] = "Peter Chege",
                ["reference"] = "ENQ-REPLAY01",
                ["detail"] = "Reply on 254712345678"
            },
            IdempotencyKey: $"storefront-enquiry:{business.Id}:ENQ-REPLAY01");

        await notifications.SendAsync(intent);
        await notifications.SendAsync(intent);

        var emails = await context.NotificationLogs.AsNoTracking()
            .Where(row => row.Channel == "email").ToListAsync();
        Assert.Single(emails);
    }

    [Fact]
    public async Task An_enquiry_survives_a_notification_pipeline_fault()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var owner = BookingTestBase.CreateOwner();
        var business = BookingTestBase.CreateBusiness(owner.Id);
        await BookingTestBase.SeedAsync(context, owner, business);

        var service = Service(context, new ThrowingNotificationService());
        var result = await service.SubmitAsync(business.Id, Request());

        // The visitor is still thanked and given a real reference: the enquiry
        // was accepted, and a delivery fault is the platform's problem, not
        // something to report back as the visitor's failure.
        Assert.True(result.Success, result.Error?.Message);
        Assert.StartsWith("ENQ-", result.Data!.Reference);
    }

    /// <summary>A pipeline that fails the way a broken provider would.</summary>
    private sealed class ThrowingNotificationService : INotificationService
    {
        public Task<NotificationResult> SendAsync(NotificationRequest request, CancellationToken ct = default) =>
            throw new InvalidOperationException("The notification pipeline is unavailable.");

        public Task<NotificationResult> QueueAsync(NotificationRequest request, CancellationToken ct = default) =>
            throw new InvalidOperationException("The notification pipeline is unavailable.");
    }
}
