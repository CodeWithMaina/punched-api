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
/// The staff financial and performance reports.
///
/// <para>These pin the definitions the owner sees: revenue is the booked service-line
/// snapshot of <b>completed</b> work, collected is settled payments, outstanding is the
/// difference, and rates are shares of decided appointments only. The window is stated
/// exactly, so the assertions cannot drift into "whatever today is".</para>
/// </summary>
public class StaffReportingTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public StaffReportingTests()
    {
        _connection = BookingTestBase.CreateConnection();
        _context = BookingTestBase.CreateContext(_connection);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private sealed record Env(
        Business Business, User Owner, User StaffA, User StaffB, User Customer,
        ServiceCatalogItem Service);

    private static Appointment Dated(
        Business business, User customer, User staff, DateTime start, string status) =>
        BookingTestBase.CreateAppointment(
            business.Id, customer.Id, staff.Id, start, start.AddHours(1), status);

    private static (Appointment Appointment, AppointmentResource Resource) Completed(
        Business business, User customer, User staff, ServiceCatalogItem service,
        DateTime start, decimal price, int minutes)
    {
        var appointment = BookingTestBase.CreateAppointment(
            business.Id, customer.Id, staff.Id, start, start.AddMinutes(minutes),
            AppointmentStatuses.Completed);

        var resource = BookingTestBase.CreateResource(
            appointment.Id, service.Id, service.Name, minutes, price, 0);

        appointment.Resources.Add(resource);
        return (appointment, resource);
    }

    private static Payment Payment(
        Business business, User customer, Appointment appointment, decimal amount, PaymentStatus status) => new()
    {
        Id = Guid.NewGuid(),
        BusinessId = business.Id,
        CustomerId = customer.Id,
        AppointmentId = appointment.Id,
        Type = PaymentType.FullPayment,
        Method = PaymentMethod.Mpesa,
        Provider = PaymentProviderKind.ManualMpesa,
        Status = status,
        Amount = amount,
        Currency = "KES",
        Reference = $"PMT-{Guid.NewGuid():N}"[..20],
        CompletedAt = status is PaymentStatus.Success or PaymentStatus.Verified ? DateTime.UtcNow : null,
        CreatedAt = DateTime.UtcNow
    };

    private async Task<Env> SeedAsync()
    {
        var owner = BookingTestBase.CreateOwner("report-owner@test.com");
        var business = BookingTestBase.CreateBusiness(owner.Id, "Report Studio");
        var staffA = BookingTestBase.CreateStaff(business.Id, "amina@test.com");
        var staffB = BookingTestBase.CreateStaff(business.Id, "brian@test.com");
        var customer = BookingTestBase.CreateCustomer("report-customer@test.com");
        var service = BookingTestBase.CreateService(business.Id, "Cut", 60, 500m);

        await BookingTestBase.SeedAsync(_context, owner, business, staffA, staffB, customer, service);

        var now = DateTime.UtcNow;

        var aFirst = Completed(business, customer, staffA, service, now.AddDays(-3), 500m, 60);
        // Second service line on the same visit — the rate card is per service, the
        // service value is per visit.
        var aFirstExtra = BookingTestBase.CreateResource(
            aFirst.Appointment.Id, service.Id, "Beard", 30, 300m, 1);
        aFirst.Appointment.Resources.Add(aFirstExtra);

        var aThird = Completed(business, customer, staffA, service, now.AddDays(-1), 400m, 30);
        var aCancelled = Dated(business, customer, staffA, now.AddDays(-4), AppointmentStatuses.Cancelled);
        var aNoShow = Dated(business, customer, staffA, now.AddDays(-4).AddHours(1), AppointmentStatuses.NoShow);
        var bFirst = Completed(business, customer, staffB, service, now.AddDays(-2), 1000m, 90);

        // Preceding window (the same length, immediately before it).
        var aPrevious = Completed(business, customer, staffA, service, now.AddDays(-40), 200m, 30);

        await BookingTestBase.SeedAsync(_context,
            aFirst.Appointment, aFirst.Resource, aFirstExtra,
            aThird.Appointment, aThird.Resource,
            aCancelled, aNoShow,
            bFirst.Appointment, bFirst.Resource,
            aPrevious.Appointment, aPrevious.Resource);

        // Money: 500 settled on A's first visit, none on the rest; B's visit paid.
        await BookingTestBase.SeedAsync(_context,
            Payment(business, customer, aFirst.Appointment, 500m, PaymentStatus.Verified),
            Payment(business, customer, aThird.Appointment, 400m, PaymentStatus.Submitted),
            Payment(business, customer, bFirst.Appointment, 1000m, PaymentStatus.Success));

        var review = new Review
        {
            Id = Guid.NewGuid(),
            AppointmentId = aFirst.Appointment.Id,
            BusinessId = business.Id,
            CustomerId = customer.Id,
            StaffUserId = staffA.Id,
            Rating = 5,
            Status = ReviewStatuses.Published,
            CreatedAt = now.AddDays(-1),
            UpdatedAt = now.AddDays(-1)
        };
        await BookingTestBase.SeedAsync(_context, review);

        return new Env(business, owner, staffA, staffB, customer, service);
    }

    private StaffReportingService CreateService(Guid ownerId, Guid businessId)
    {
        var scope = new Mock<IBusinessScopeResolver>();
        scope.Setup(s => s.GetOwnedBusinessIdAsync(ownerId)).ReturnsAsync(businessId);
        scope.Setup(s => s.GetMemberBusinessIdAsync(It.IsAny<Guid>())).ReturnsAsync(businessId);

        return new StaffReportingService(
            _context, scope.Object, TestHelpers.CreateLogger<StaffReportingService>());
    }

    [Fact]
    public async Task Financials_UseTheBookedPriceSnapshot_AndSeparateBilledFromCollected()
    {
        var env = await SeedAsync();
        var service = CreateService(env.Owner.Id, env.Business.Id);

        var result = await service.GetFinancialsAsync(env.Owner.Id, new StaffReportQuery { Range = "30d" });

        Assert.True(result.Success, result.Error?.Message);
        var report = result.Data!;

        var amina = report.Items.Single(i => i.StaffUserId == env.StaffA.Id);
        var brian = report.Items.Single(i => i.StaffUserId == env.StaffB.Id);

        Assert.Equal(2, amina.CompletedAppointments);
        Assert.Equal(1, amina.CancelledAppointments);
        Assert.Equal(1, amina.NoShowAppointments);
        Assert.Equal(1200m, amina.ServiceValue);
        Assert.Equal(1200m, amina.RevenueAttributed);
        Assert.Equal(3, amina.CompletedServiceLines);
        Assert.Equal(600m, amina.AverageServiceValue);   // per visit
        Assert.Equal(400m, amina.AverageRateCard);       // per service line
        Assert.Equal(500m, amina.AmountCollected);
        Assert.Equal(700m, amina.OutstandingAmount);     // 1200 billed - 500 received
        Assert.Equal(41.7m, amina.CollectionRate);
        Assert.Equal(1, amina.PendingVerificationCount);
        Assert.Equal(54.5m, amina.RevenueSharePercent);  // 1200 / 2200

        Assert.Equal(1000m, brian.ServiceValue);
        Assert.Equal(1000m, brian.AmountCollected);
        Assert.Equal(0m, brian.OutstandingAmount);
        Assert.Equal(100m, brian.CollectionRate);

        Assert.Equal(2200m, report.Totals.TotalRevenue);
        Assert.Equal(1500m, report.Totals.TotalCollected);
        Assert.Equal(700m, report.Totals.TotalOutstanding);
        Assert.Equal(3, report.Totals.CompletedAppointments);
        Assert.Equal("appointment_snapshot", report.RateCardSource);
        Assert.Equal("30d", report.Period.Range);
    }

    [Fact]
    public async Task Performance_RatesDecidedWork_ComparesThePreviousWindow_AndRanksByRevenue()
    {
        var env = await SeedAsync();
        var service = CreateService(env.Owner.Id, env.Business.Id);

        var result = await service.GetPerformanceAsync(env.Owner.Id, new StaffReportQuery { Range = "30d" });

        Assert.True(result.Success, result.Error?.Message);
        var report = result.Data!;

        var amina = report.Items.Single(i => i.StaffUserId == env.StaffA.Id);
        var brian = report.Items.Single(i => i.StaffUserId == env.StaffB.Id);

        // 2 completed, 1 cancelled, 1 no-show = 4 decided.
        Assert.Equal(50m, amina.CompletionRate);
        Assert.Equal(25m, amina.CancellationRate);
        Assert.Equal(25m, amina.NoShowRate);
        Assert.Equal(1200m, amina.RevenueAttributed);
        Assert.Equal(600m, amina.AverageServiceValue);
        Assert.Equal(60m, amina.AverageAppointmentMinutes); // 120 resource minutes / 2 visits
        Assert.Equal(100m, amina.RepeatCustomerRate);       // both visits same customer
        Assert.Equal(5m, amina.Rating);
        Assert.Equal(1, amina.ReviewCount);
        Assert.Equal(200m, amina.PreviousRevenue);
        Assert.Equal(500m, amina.RevenueChangePercent);     // 1200 vs 200
        Assert.Equal("up", amina.Trend);
        Assert.Equal(1, amina.Rank);
        Assert.True(amina.HasSufficientData);

        Assert.Equal(100m, brian.CompletionRate);
        Assert.Null(brian.Rating);
        Assert.Equal(2, brian.Rank);

        Assert.Equal(60m, report.Totals.CompletionRate);    // 3 completed of 5 decided
        Assert.Equal(5m, report.Totals.AverageRating);
    }

    [Fact]
    public async Task ReportingWindow_UsesBusinessLocalDays_AndSpansDstCorrectly()
    {
        var env = await SeedAsync();

        // New York springs forward on 2026-03-08: that local day is 23 hours long.
        env.Business.TimeZoneId = "America/New_York";
        await _context.SaveChangesAsync();

        var inDay = Completed(env.Business, env.Customer, env.StaffA, env.Service,
            new DateTime(2026, 3, 9, 3, 0, 0, DateTimeKind.Utc), 100m, 30);   // 23:00 local, 8 Mar
        var beforeDay = Completed(env.Business, env.Customer, env.StaffA, env.Service,
            new DateTime(2026, 3, 8, 4, 30, 0, DateTimeKind.Utc), 200m, 30);  // 23:30 local, 7 Mar
        var afterDay = Completed(env.Business, env.Customer, env.StaffA, env.Service,
            new DateTime(2026, 3, 9, 4, 30, 0, DateTimeKind.Utc), 400m, 30);  // 00:30 local, 9 Mar

        await BookingTestBase.SeedAsync(_context,
            inDay.Appointment, inDay.Resource,
            beforeDay.Appointment, beforeDay.Resource,
            afterDay.Appointment, afterDay.Resource);

        var service = CreateService(env.Owner.Id, env.Business.Id);
        var result = await service.GetFinancialsAsync(env.Owner.Id, new StaffReportQuery
        {
            From = new DateOnly(2026, 3, 8),
            To = new DateOnly(2026, 3, 8)
        });

        Assert.True(result.Success, result.Error?.Message);
        var report = result.Data!;

        Assert.Equal("America/New_York", report.Period.TimeZoneId);
        // Local midnight is 05:00Z (EST); next local midnight is 04:00Z (EDT).
        Assert.Equal(new DateTime(2026, 3, 8, 5, 0, 0, DateTimeKind.Utc), report.Period.FromUtc);
        Assert.Equal(new DateTime(2026, 3, 9, 4, 0, 0, DateTimeKind.Utc), report.Period.ToUtcExclusive);
        Assert.Equal(TimeSpan.FromHours(23), report.Period.ToUtcExclusive - report.Period.FromUtc);

        // Only the appointment inside the shop's local day is counted.
        Assert.Equal(100m, report.Totals.TotalRevenue);
        Assert.Equal(1, report.Totals.CompletedAppointments);
    }

    [Fact]
    public async Task StaffAndFinanceWindows_ShareTheSameBusinessLocalBoundaries()
    {
        var env = await SeedAsync();

        // Fixed dates (not "today") so the assertion cannot straddle a midnight.
        env.Business.TimeZoneId = "America/New_York";
        await _context.SaveChangesAsync();
        var timeZone = BookingTime.ResolveTimeZone("America/New_York");
        var finance = FinancialPeriod.Resolve(
            FinancialPeriod.Custom,
            new DateTime(2026, 3, 8),
            new DateTime(2026, 3, 8),
            timeZone);

        var service = CreateService(env.Owner.Id, env.Business.Id);
        var staff = await service.GetFinancialsAsync(env.Owner.Id, new StaffReportQuery
        {
            From = new DateOnly(2026, 3, 8),
            To = new DateOnly(2026, 3, 8)
        });

        Assert.True(staff.Success, staff.Error?.Message);
        Assert.Equal(finance.FromUtc, staff.Data!.Period.FromUtc);
        Assert.Equal(finance.ToUtc, staff.Data.Period.ToUtcExclusive);
    }

    [Fact]
    public async Task Reports_AreTenantScoped_AndNeverIncludeAnotherBusinessesStaff()
    {
        var env = await SeedAsync();

        var otherOwner = BookingTestBase.CreateOwner("other-report-owner@test.com");
        var otherBusiness = BookingTestBase.CreateBusiness(otherOwner.Id, "Other Studio");
        await BookingTestBase.SeedAsync(_context, otherOwner, otherBusiness);

        var service = CreateService(otherOwner.Id, otherBusiness.Id);
        var result = await service.GetFinancialsAsync(otherOwner.Id, new StaffReportQuery { Range = "30d" });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Empty(result.Data!.Items);
        Assert.Equal(0m, result.Data.Totals.TotalRevenue);
    }
}
