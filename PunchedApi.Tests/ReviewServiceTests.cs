using System.Reflection;
using System.Text.Json;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PunchedApi.API.Controllers;
using Microsoft.AspNetCore.Mvc.Routing;

using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;
using PunchedApi.Application.Validators;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Tests;

public sealed class ReviewServiceTests
{
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static async Task<(ApplicationDbContext Context, ReviewService Service, User Customer, User Other, Business Business, Appointment Appointment)> EnvAsync(DateTimeOffset now)
    {
        var connection = BookingTestBase.CreateConnection();
        var context = BookingTestBase.CreateContext(connection);
        var owner = BookingTestBase.CreateOwner();
        var customer = BookingTestBase.CreateCustomer("reviewer@test.com");
        customer.FullName = "Jane Reviewer";
        var other = BookingTestBase.CreateCustomer("other@test.com");
        var business = BookingTestBase.CreateBusiness(owner.Id);
        var appointment = BookingTestBase.CreateAppointment(business.Id, customer.Id, null, now.UtcDateTime.AddDays(-1), now.UtcDateTime, "completed");
        var history = BookingTestBase.CreateHistory(appointment.Id, "completed", owner.Id);
        history.ChangedAt = now.UtcDateTime.AddDays(-1);
        await BookingTestBase.SeedAsync(context, owner, customer, other, business, appointment, history);
        return (context, new ReviewService(context, new FixedTimeProvider(now)), customer, other, business, appointment);
    }

    [Fact]
    public async Task Create_DerivesBusinessAndPublishedStatus_AndTrimsComment()
    {
        var e = await EnvAsync(DateTimeOffset.UtcNow);
        var result = await e.Service.CreateAsync(e.Customer.Id, new CreateReviewRequest { AppointmentId = e.Appointment.Id, Rating = 5, Comment = "  Great  " });
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(e.Business.Id, result.Data!.BusinessId);
        Assert.Equal(ReviewStatuses.Published, result.Data.Status);
        Assert.Equal("Great", result.Data.Comment);
        Assert.Equal(e.Appointment.StaffUserId, (await e.Context.Reviews.SingleAsync()).StaffUserId);
    }

    [Theory]
    [InlineData(-30, true)]
    [InlineData(-30.0001, false)]
    public async Task Create_EnforcesThirtyDayBoundary(double daysAgo, bool expected)
    {
        var now = DateTimeOffset.UtcNow;
        var e = await EnvAsync(now);
        e.Context.AppointmentStatusHistory.Single().ChangedAt = now.UtcDateTime.AddDays(daysAgo);
        await e.Context.SaveChangesAsync();
        var result = await e.Service.CreateAsync(e.Customer.Id, new CreateReviewRequest { AppointmentId = e.Appointment.Id, Rating = 4 });
        Assert.Equal(expected, result.Success);
        if (!expected) Assert.Equal("REVIEW_NOT_ELIGIBLE", result.Error?.Code);
    }

    [Fact]
    public async Task Create_WrongOwnerMissingAndIncompleteReturnSameNonEnumeratingCode()
    {
        var e = await EnvAsync(DateTimeOffset.UtcNow);
        var wrongOwner = await e.Service.CreateAsync(e.Other.Id, new CreateReviewRequest { AppointmentId = e.Appointment.Id, Rating = 4 });
        var missing = await e.Service.CreateAsync(e.Customer.Id, new CreateReviewRequest { AppointmentId = Guid.NewGuid(), Rating = 4 });
        e.Appointment.Status = "confirmed"; await e.Context.SaveChangesAsync();
        var incomplete = await e.Service.CreateAsync(e.Customer.Id, new CreateReviewRequest { AppointmentId = e.Appointment.Id, Rating = 4 });
        Assert.Equal("REVIEW_NOT_ELIGIBLE", wrongOwner.Error?.Code);
        Assert.Equal("REVIEW_NOT_ELIGIBLE", missing.Error?.Code);
        Assert.Equal("REVIEW_NOT_ELIGIBLE", incomplete.Error?.Code);
        Assert.Equal(wrongOwner.Error?.Message, missing.Error?.Message);
    }

    [Fact]
    public async Task Create_DuplicateReturnsStableConflictCode()
    {
        var e = await EnvAsync(DateTimeOffset.UtcNow);
        Assert.True((await e.Service.CreateAsync(e.Customer.Id, new CreateReviewRequest { AppointmentId = e.Appointment.Id, Rating = 5 })).Success);
        var duplicate = await e.Service.CreateAsync(e.Customer.Id, new CreateReviewRequest { AppointmentId = e.Appointment.Id, Rating = 4 });
        Assert.Equal("REVIEW_ALREADY_EXISTS", duplicate.Error?.Code);
    }

    [Fact]
    public async Task Update_AllowsInsideSevenDays_AndExpiresAfterBoundaryFromCreatedAt()
    {
        var now = DateTimeOffset.UtcNow;
        var e = await EnvAsync(now);
        Assert.True((await e.Service.CreateAsync(e.Customer.Id, new CreateReviewRequest { AppointmentId = e.Appointment.Id, Rating = 5 })).Success);
        var review = await e.Context.Reviews.SingleAsync();
        review.CreatedAt = now.UtcDateTime.AddDays(-6); await e.Context.SaveChangesAsync();
        Assert.True((await e.Service.UpdateAsync(e.Customer.Id, review.Id, new UpdateReviewRequest { Rating = 4 })).Success);
        review = await e.Context.Reviews.SingleAsync();
        review.CreatedAt = now.UtcDateTime.AddDays(-7).AddSeconds(-1); await e.Context.SaveChangesAsync();
        var expired = await e.Service.UpdateAsync(e.Customer.Id, review.Id, new UpdateReviewRequest { Rating = 3 });
        Assert.Equal("REVIEW_EDIT_WINDOW_EXPIRED", expired.Error?.Code);
    }

    [Fact]
    public async Task Update_OtherCustomerReturnsNotFound()
    {
        var e = await EnvAsync(DateTimeOffset.UtcNow);
        Assert.True((await e.Service.CreateAsync(e.Customer.Id, new CreateReviewRequest { AppointmentId = e.Appointment.Id, Rating = 5 })).Success);
        var review = await e.Context.Reviews.SingleAsync();
        Assert.Equal("NOT_FOUND", (await e.Service.UpdateAsync(e.Other.Id, review.Id, new UpdateReviewRequest { Rating = 1 })).Error?.Code);
    }

    [Fact]
    public async Task PublicListAndSummary_ScopeBusinessExcludeHiddenAndUseApprovedName()
    {
        var e = await EnvAsync(DateTimeOffset.UtcNow);
        e.Context.Reviews.Add(new Review
        {
            Id = Guid.NewGuid(), AppointmentId = e.Appointment.Id, BusinessId = e.Business.Id,
            CustomerId = e.Other.Id, Rating = 1, Status = ReviewStatuses.Hidden, CreatedAt = DateTime.UtcNow
        });
        await e.Context.SaveChangesAsync();
        Assert.True((await e.Service.CreateAsync(e.Customer.Id, new CreateReviewRequest { AppointmentId = e.Appointment.Id, Rating = 4 })).Success);

        var list = await e.Service.GetBusinessReviewsAsync(e.Business.Id, 1, 20);
        var summary = await e.Service.GetSummaryAsync(e.Business.Id);
        Assert.True(list.Success);
        Assert.True(summary.Success);
        Assert.Single(list.Data!.Items);
        Assert.Equal(1, summary.Data!.TotalCount);
        Assert.Equal(4m, summary.Data.AverageRating);
        Assert.Equal("Jane R.", list.Data.Items[0].ReviewerDisplayName);

        var missing = await e.Service.GetBusinessReviewsAsync(Guid.NewGuid(), 1, 20);
        Assert.False(missing.Success);
        Assert.Equal("NOT_FOUND", missing.Error?.Code);
    }

    [Fact]
    public async Task OwnerList_IsTenantScopedAndIncludesHiddenReadOnly()
    {
        var e = await EnvAsync(DateTimeOffset.UtcNow);
        e.Context.Reviews.Add(new Review
        {
            Id = Guid.NewGuid(), AppointmentId = e.Appointment.Id, BusinessId = e.Business.Id,
            CustomerId = e.Customer.Id, Rating = 1, Status = ReviewStatuses.Hidden, CreatedAt = DateTime.UtcNow
        });
        await e.Context.SaveChangesAsync();
        var result = await e.Service.GetOwnerReviewsAsync(e.Business.Id, 1, 20);
        Assert.True(result.Success);
        Assert.Single(result.Data!.Items);
        Assert.Equal(ReviewStatuses.Hidden, result.Data.Items[0].Status);
    }

    [Fact]
    public void PublicDto_AndValidators_MatchV1Contract()
    {
        var serializedProperties = JsonSerializer.SerializeToElement(new PublicReviewResponse()).EnumerateObject().Select(p => p.Name).ToArray();
        Assert.DoesNotContain("customerId", serializedProperties);
        Assert.DoesNotContain("appointmentId", serializedProperties);
        Assert.DoesNotContain("status", serializedProperties);
        Assert.Contains("reviewerDisplayName", serializedProperties);

        var validator = new CreateReviewRequestValidator();
        Assert.False(validator.Validate(new CreateReviewRequest { AppointmentId = Guid.NewGuid(), Rating = 0 }).IsValid);
        Assert.False(validator.Validate(new CreateReviewRequest { AppointmentId = Guid.NewGuid(), Rating = 5, Comment = new string('x', 501) }).IsValid);
    }

    [Fact]
    public void Schema_UsesUniqueCustomerAppointmentAndSafeStatusDefault()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var entity = context.Model.FindEntityType(typeof(Review))!;
        Assert.False(entity.FindProperty(nameof(Review.AppointmentId))!.IsNullable);
        var unique = entity.GetIndexes().Single(i =>
            i.IsUnique
            && i.Properties.Select(p => p.Name).SequenceEqual(new[] { nameof(Review.CustomerId), nameof(Review.AppointmentId) }));
        Assert.Equal(ReviewStatuses.Hidden, entity.FindProperty(nameof(Review.Status))!.GetDefaultValue());
    }

    [Fact]
    public void Controller_ContainsOnlySevenV1Methods()
    {
        var methods = typeof(ReviewController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .SelectMany(m => m.GetCustomAttributes<HttpMethodAttribute>())
            .SelectMany(a => a.HttpMethods).ToArray();
        Assert.Equal(7, methods.Length);
        Assert.DoesNotContain("DELETE", methods);
        Assert.DoesNotContain(methods, m => m.Contains("response", StringComparison.OrdinalIgnoreCase)
            || m.Contains("report", StringComparison.OrdinalIgnoreCase)
            || m.Contains("admin", StringComparison.OrdinalIgnoreCase));
    }

    // TESTS
}
