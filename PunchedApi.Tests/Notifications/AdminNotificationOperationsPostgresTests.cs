using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PunchedApi.API.Controllers;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Notifications;
using PunchedApi.Application.Services;
using PunchedApi.Application.Settings;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Tests.Notifications;

public sealed class AdminNotificationOperationsPostgresTests : IClassFixture<NotificationOutboxPostgresFixture>
{
    private readonly NotificationOutboxPostgresFixture _fixture;

    public AdminNotificationOperationsPostgresTests(NotificationOutboxPostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public void Controller_requires_platform_admin_role()
    {
        var authorize = typeof(AdminNotificationsController).GetCustomAttribute<AuthorizeAttribute>();

        Assert.NotNull(authorize);
        Assert.Equal("Admin", authorize!.Roles);
    }

    [RequiresNotificationPostgresFact]
    public async Task Overview_failure_filters_details_and_retry_use_real_ledger_data()
    {
        var now = DateTime.UtcNow;
        var user = BookingTestBase.CreateCustomer("notification-ops-admin@test.com");
        var businessId = Guid.NewGuid();
        var unassociatedBusiness = new Business
        {
            Id = businessId,
            Name = "Unassociated business",
            Category = "Cafe",
            Location = "Nairobi",
            MpesaNumber = "254700000001"
        };
        var sentId = Guid.NewGuid();
        var retryId = Guid.NewGuid();
        var permanentId = Guid.NewGuid();
        var pendingId = Guid.NewGuid();
        var processingId = Guid.NewGuid();
        var unassociatedRetryId = Guid.NewGuid();
        var futureId = Guid.NewGuid();

        await using var seed = _fixture.CreateContext();
        seed.Users.Add(user);
        seed.Businesses.Add(unassociatedBusiness);
        seed.NotificationLogs.AddRange(
            CreateRow(sentId, user.Id, now, "sent", AttemptsHistory(
                (now.AddMinutes(-20), "retryable_failure"), (now.AddMinutes(-10), "sent")),
                sentAt: now.AddMinutes(-10)),
            CreateRow(retryId, user.Id, now.AddMinutes(-5), "failed", AttemptsHistory(
                (now.AddMinutes(-30), "retryable_failure"),
                (now.AddMinutes(-15), "retryable_failure"),
                (now.AddMinutes(-5), "retryable_failure")),
                error: "retry_exhausted: private-provider-body recipient@example.com", attempts: 3,
                businessId: null),
            CreateRow(permanentId, user.Id, now.AddMinutes(-4), "failed", AttemptsHistory(
                (now.AddMinutes(-4), "permanent_failure")), error: "permanent:bad configuration"),
            CreateRow(pendingId, user.Id, now.AddMinutes(-2), "pending", EmptyHistory()),
            CreateRow(processingId, user.Id, now.AddMinutes(-1), "processing", EmptyHistory()),
            CreateRow(unassociatedRetryId, user.Id, now.AddMinutes(-3), "failed", AttemptsHistory(
                (now.AddMinutes(-3), "retryable_failure")), error: "retry_exhausted:timeout", attempts: 3,
                businessId: businessId),
            CreateRow(futureId, user.Id, now.AddMinutes(5), "sent", AttemptsHistory(
                (now.AddMinutes(5), "sent")), sentAt: now.AddMinutes(5)));
        await seed.SaveChangesAsync();

        await using var context = _fixture.CreateContext();
        var service = CreateService(context, new TestNotificationChannel { Name = NotificationChannel.Email });
        var overview = await service.GetOverviewAsync("1h");

        Assert.True(overview.AttemptHistoryComplete);
        Assert.Equal(7, overview.TotalAttempts);
        Assert.Equal(1, overview.SuccessfulDeliveries);
        Assert.Equal(3, overview.TerminalFailures);
        Assert.Equal(2, overview.RetryExhaustedNotifications);
        Assert.Equal(1, overview.PendingNotifications);
        Assert.Equal(1, overview.ProcessingNotifications);
        Assert.Equal(25d, overview.DeliverySuccessRatePercent!.Value, precision: 4);
        var trends = await service.GetTrendsAsync("1h");
        Assert.True(trends.AttemptHistoryComplete);
        Assert.Equal(7, trends.TotalAttempts);
        Assert.Equal(3, trends.RetryAttempts);
        Assert.Equal(1, trends.SuccessfulAttempts);
        Assert.Equal(6, trends.FailedAttempts);
        Assert.Contains(trends.Points, point => point.SuccessfulAttempts == 1);
        Assert.Contains(overview.Channels, channel => channel.Channel == "email" && channel.ActiveInRange);

        var failures = await service.GetFailuresAsync(new AdminNotificationFailureQuery
        {
            Range = "1h",
            Channel = NotificationChannel.Email,
            Classification = "retry_exhausted",
            RetryEligible = true,
            Search = retryId.ToString(),
            Page = 1,
            PageSize = 10
        });
        var failure = Assert.Single(failures.Items);
        Assert.Equal(retryId, failure.Id);
        Assert.True(failure.RetryEligible);

        var detail = await service.GetFailureDetailAsync(retryId);
        Assert.NotNull(detail);
        Assert.DoesNotContain("recipient@example.com", detail!.SanitizedError);
        Assert.DoesNotContain("private-provider-body", detail.SanitizedError);

        await using var retryContextA = _fixture.CreateContext();
        await using var retryContextB = _fixture.CreateContext();
        var concurrentRetries = await Task.WhenAll(
            CreateService(retryContextA, new TestNotificationChannel { Name = NotificationChannel.Email }).RetryAsync(retryId),
            CreateService(retryContextB, new TestNotificationChannel { Name = NotificationChannel.Email }).RetryAsync(retryId));
        Assert.Single(concurrentRetries, decision => decision.WasAccepted);
        Assert.Single(concurrentRetries, decision => !decision.WasAccepted);

        var permanent = await service.RetryAsync(permanentId);
        Assert.False(permanent.WasAccepted);
        var alreadySent = await service.RetryAsync(sentId);
        Assert.Equal("RETRY_NOT_ELIGIBLE", alreadySent.Code);
        var unassociated = await service.RetryAsync(unassociatedRetryId);
        Assert.Equal("TENANT_ASSOCIATION_CHANGED", unassociated.Code);

        context.ChangeTracker.Clear();
        var requeued = await context.NotificationLogs.SingleAsync(row => row.Id == retryId);
        Assert.Equal("pending", requeued.Status);
        Assert.Equal(0, requeued.Attempts);
        Assert.Equal("failed", (await context.NotificationLogs.SingleAsync(row => row.Id == permanentId)).Status);

        await using var cleanup = _fixture.CreateContext();
        await cleanup.NotificationLogs.Where(row =>
            row.Id == sentId || row.Id == retryId || row.Id == permanentId || row.Id == pendingId ||
            row.Id == processingId || row.Id == unassociatedRetryId || row.Id == futureId).ExecuteDeleteAsync();
        await cleanup.Businesses.Where(business => business.Id == businessId).ExecuteDeleteAsync();
    }

    [RequiresNotificationPostgresFact]
    public async Task Legacy_external_attempts_are_reported_incomplete_not_invented()
    {
        var now = DateTime.UtcNow;
        var user = BookingTestBase.CreateCustomer("notification-ops-legacy@test.com");
        var id = Guid.NewGuid();
        await using (var seed = _fixture.CreateContext())
        {
            seed.Users.Add(user);
            var legacyRow = CreateRow(
                id, user.Id, now, "failed", EmptyHistory(),
                error: "permanent:legacy provider response unavailable");
            legacyRow.DeliveryAttemptsJson = null;
            seed.NotificationLogs.Add(legacyRow);
            await seed.SaveChangesAsync();
        }

        await using var context = _fixture.CreateContext();
        var service = CreateService(context, new TestNotificationChannel { Name = NotificationChannel.Email });
        var overview = await service.GetOverviewAsync("1h");
        var trends = await service.GetTrendsAsync("1h");

        Assert.False(overview.AttemptHistoryComplete);
        Assert.Null(overview.TotalAttempts);
        Assert.Null(overview.RetryAttempts);
        Assert.False(trends.AttemptHistoryComplete);
        Assert.Null(trends.TotalAttempts);
        Assert.Null(trends.RetryAttempts);

        await using var cleanup = _fixture.CreateContext();
        await cleanup.NotificationLogs.Where(row => row.Id == id).ExecuteDeleteAsync();
    }

    private static AdminNotificationOperationsService CreateService(
        PunchedApi.Infrastructure.Data.ApplicationDbContext context,
        params INotificationChannel[] channels) =>
        new(context, channels, Options.Create(new EmailSettings
        {
            Enabled = true,
            Host = "smtp.test.invalid",
            FromAddress = "sender@example.com"
        }));

    private static NotificationLog CreateRow(
        Guid id,
        Guid userId,
        DateTime updatedAt,
        string status,
        string history,
        string? error = null,
        int attempts = 0,
        Guid? businessId = null,
        DateTime? sentAt = null) => new()
    {
        Id = id,
        UserId = userId,
        BusinessId = businessId,
        Channel = NotificationChannel.Email,
        TemplateType = "appointment.booked",
        Status = status,
        SentAt = sentAt ?? updatedAt,
        PayloadJson = "{}",
        Attempts = attempts,
        DeliveryAttemptsJson = history,
        IdempotencyKey = $"ops-test:{id:N}",
        Error = error,
        UpdatedAt = updatedAt,
        CreatedAt = updatedAt
    };

    private static string EmptyHistory() => JsonSerializer.Serialize(new
    {
        version = 1,
        complete = true,
        attempts = Array.Empty<object>()
    });

    private static string AttemptsHistory(params (DateTime StartedAtUtc, string Outcome)[] attempts) =>
        JsonSerializer.Serialize(new
        {
            version = 1,
            complete = true,
            attempts = attempts.Select(item => new
            {
                startedAtUtc = item.StartedAtUtc,
                completedAtUtc = item.StartedAtUtc,
                outcome = item.Outcome
            })
        });
}