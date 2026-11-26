using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Tests;

/// <summary>
/// The workforce summary behind the Staff overview.
///
/// <para><b>The invariant worth pinning.</b> The overview shows revenue, collection and
/// quality rates, and the Financials/Performance tabs show the same numbers per person.
/// They may differ only by aggregation — never by definition. These tests assert the
/// totals are identical to the tab totals for the same window, so a change to one
/// report cannot silently make the dashboard disagree with it.</para>
/// </summary>
public class StaffOverviewSummaryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public StaffOverviewSummaryTests()
    {
        _connection = BookingTestBase.CreateConnection();
        _context = BookingTestBase.CreateContext(_connection);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private StaffReportingService CreateService(Guid ownerId, Guid businessId)
    {
        var scope = new Mock<IBusinessScopeResolver>();
        scope.Setup(s => s.GetOwnedBusinessIdAsync(ownerId)).ReturnsAsync(businessId);
        scope.Setup(s => s.GetMemberBusinessIdAsync(It.IsAny<Guid>())).ReturnsAsync(businessId);

        return new StaffReportingService(
            _context, scope.Object, TestHelpers.CreateLogger<StaffReportingService>());
    }

    private static (Appointment Appointment, AppointmentResource Resource) Completed(
        Business business, User customer, User staff, ServiceCatalogItem service,
        DateTime start, decimal price)
    {
        var appointment = BookingTestBase.CreateAppointment(
            business.Id, customer.Id, staff.Id, start, start.AddHours(1),
            AppointmentStatuses.Completed);

        var resource = BookingTestBase.CreateResource(
            appointment.Id, service.Id, service.Name, 60, price, 0);

        appointment.Resources.Add(resource);
        return (appointment, resource);
    }

    [Fact]
    public async Task Summary_totals_are_the_same_numbers_the_tabs_report()
    {
        var owner = BookingTestBase.CreateOwner("summary-owner@test.com");
        var business = BookingTestBase.CreateBusiness(owner.Id, "Summary Studio");
        var member = BookingTestBase.CreateStaff(business.Id, "summary-member@test.com");
        var customer = BookingTestBase.CreateCustomer("summary-customer@test.com");
        var service = BookingTestBase.CreateService(business.Id, "Cut", 60, 900m);

        await BookingTestBase.SeedAsync(_context, owner, business, member, customer, service);

        var now = DateTime.UtcNow;

        var done = Completed(business, customer, member, service, now.AddDays(-2), 900m);
        var cancelled = BookingTestBase.CreateAppointment(
            business.Id, customer.Id, member.Id,
            now.AddDays(-1), now.AddDays(-1).AddHours(1), AppointmentStatuses.Cancelled);

        await BookingTestBase.SeedAsync(_context, done.Appointment, done.Resource, cancelled);

        await BookingTestBase.SeedAsync(_context, new Payment
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            CustomerId = customer.Id,
            AppointmentId = done.Appointment.Id,
            Type = PaymentType.FullPayment,
            Method = PaymentMethod.Cash,
            Provider = PaymentProviderKind.Cash,
            Status = PaymentStatus.Success,
            Amount = 600m,
            Currency = "KES",
            Reference = "SUMMARY-CASH-1",
            CompletedAt = now.AddDays(-2),
            CreatedAt = now.AddDays(-2)
        });

        var reporting = CreateService(owner.Id, business.Id);
        var query = new StaffReportQuery { Range = "30d" };

        var summary = await reporting.GetOverviewSummaryAsync(owner.Id, query);
        var financials = await reporting.GetFinancialsAsync(owner.Id, query);
        var performance = await reporting.GetPerformanceAsync(owner.Id, query);

        Assert.True(summary.Success, summary.Error?.Message);
        var totals = summary.Data!;

        Assert.Equal(financials.Data!.Totals.TotalRevenue, totals.RevenueAttributed);
        Assert.Equal(financials.Data.Totals.TotalCollected, totals.AmountCollected);
        Assert.Equal(financials.Data.Totals.TotalOutstanding, totals.OutstandingAmount);
        Assert.Equal(financials.Data.Totals.CompletedAppointments, totals.CompletedAppointments);
        Assert.Equal(financials.Data.Totals.CancelledAppointments, totals.CancelledAppointments);
        Assert.Equal(financials.Data.Totals.AverageRateCard, totals.AverageRateCard);
        Assert.Equal(performance.Data!.Totals.CompletionRate, totals.CompletionRate);

        Assert.Equal(1, totals.TotalStaff);
        Assert.Equal(1, totals.ActiveStaff);
        Assert.Equal(2, totals.AppointmentsHandled); // one completed, one cancelled
        Assert.Equal(900m, totals.RevenueAttributed);
        Assert.Equal(600m, totals.AmountCollected);
        Assert.Equal(300m, totals.OutstandingAmount);
        Assert.True(totals.HasFinancialData);
        Assert.True(totals.HasPerformanceData);
    }

    [Fact]
    public async Task Summary_reports_no_data_rather_than_zero_performance()
    {
        var owner = BookingTestBase.CreateOwner("empty-owner@test.com");
        var business = BookingTestBase.CreateBusiness(owner.Id, "Empty Studio");
        var member = BookingTestBase.CreateStaff(business.Id, "empty-member@test.com");

        await BookingTestBase.SeedAsync(_context, owner, business, member);

        var summary = await CreateService(owner.Id, business.Id)
            .GetOverviewSummaryAsync(owner.Id, new StaffReportQuery { Range = "7d" });

        Assert.True(summary.Success, summary.Error?.Message);
        var totals = summary.Data!;

        Assert.Equal(1, totals.TotalStaff);
        Assert.Equal(0, totals.AppointmentsHandled);
        Assert.Equal(0m, totals.RevenueAttributed);
        // The client shows "statistics appear after the first completed appointment"
        // instead of ranking a brand-new team at zero percent.
        Assert.False(totals.HasFinancialData);
        Assert.False(totals.HasPerformanceData);
        Assert.Null(totals.AverageRating);
    }
}
