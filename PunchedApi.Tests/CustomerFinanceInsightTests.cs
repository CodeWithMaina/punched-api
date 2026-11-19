using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;
using PunchedApi.Infrastructure.Repositories;

namespace PunchedApi.Tests;

/// <summary>
/// The financial block of GET /businesses/me/customers/{id}/details.
///
/// The figures must come from this business's own recognised money — verified income
/// minus verified refunds, never pending, rejected or voided rows — and from completed
/// appointments only. The tests also pin the isolation rules: another customer's rows
/// and another business's rows cannot leak into a customer's totals.
/// </summary>
public class CustomerFinanceInsightTests
{
    private sealed record Env(
        ApplicationDbContext Ctx,
        BusinessService Svc,
        Guid OwnerId,
        Business Business,
        User Customer);

    private static User AddUser(ApplicationDbContext ctx, string email, string fullName, UserRole role)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            FullName = fullName,
            Role = role,
            CreatedAt = DateTime.UtcNow,
            Auth = new UserAuth
            {
                Id = Guid.NewGuid(),
                Email = email,
                PasswordHash = "x",
                IsVerified = true,
                CreatedAt = DateTime.UtcNow
            }
        };
        ctx.Users.Add(user);
        return user;
    }

    private static FinancialTransaction Income(
        Guid businessId,
        Guid? customerId,
        decimal amount,
        DateTime paidAt,
        FinancialVerificationStatus verification = FinancialVerificationStatus.Verified,
        bool voided = false) =>
        new()
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            CustomerId = customerId,
            CustomerName = "Customer",
            Type = FinancialTransactionType.Income,
            Source = TransactionSource.PunchedBooking,
            PaymentMethod = FinancialPaymentMethod.MpesaTill,
            VerificationStatus = verification,
            ReconciliationStatus = ReconciliationStatus.Unreconciled,
            Amount = amount,
            Currency = "KES",
            Reference = $"FIN-{Guid.NewGuid():N}"[..12],
            TransactionDate = paidAt,
            RecordedAt = paidAt,
            VerifiedAt = verification == FinancialVerificationStatus.Verified ? paidAt : null,
            IsVoided = voided,
            CreatedAt = paidAt
        };

    private static FinancialTransaction Refund(Guid businessId, Guid customerId, decimal amount, DateTime at) =>
        new()
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            CustomerId = customerId,
            Type = FinancialTransactionType.Refund,
            Source = TransactionSource.PunchedBooking,
            PaymentMethod = FinancialPaymentMethod.MpesaTill,
            VerificationStatus = FinancialVerificationStatus.Verified,
            ReconciliationStatus = ReconciliationStatus.Unreconciled,
            Amount = amount,
            Currency = "KES",
            Reference = $"FIN-{Guid.NewGuid():N}"[..12],
            TransactionDate = at,
            RecordedAt = at,
            VerifiedAt = at,
            CreatedAt = at
        };

    private static Appointment AppointmentWithService(
        Guid businessId,
        Guid customerId,
        Guid serviceId,
        string serviceName,
        string status,
        int dayOffset)
    {
        var start = DateTime.UtcNow.AddDays(dayOffset);
        var appointment = new Appointment
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            CustomerId = customerId,
            ScheduledAt = start,
            EndAt = start.AddMinutes(60),
            Status = status,
            CreatedAt = start
        };
        appointment.Resources.Add(new AppointmentResource
        {
            Id = Guid.NewGuid(),
            AppointmentId = appointment.Id,
            ServiceCatalogItemId = serviceId,
            Name = serviceName,
            DurationMinutes = 60,
            Price = 1000,
            CreatedAt = start
        });
        return appointment;
    }


    private static Env CreateEnv()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(conn).Options;
        var ctx = new ApplicationDbContext(options);
        ctx.Database.EnsureCreated();

        var owner = AddUser(ctx, "owner@t.com", "Owner", UserRole.Business);
        var otherOwner = AddUser(ctx, "other@t.com", "Other Owner", UserRole.Business);
        var customer = AddUser(ctx, "customer@t.com", "Amina Wanjiku", UserRole.Customer);
        var otherCustomer = AddUser(ctx, "other-customer@t.com", "Beatrice Kim", UserRole.Customer);

        var business = new Business
        {
            Id = Guid.NewGuid(),
            Name = "Kudu Studio",
            Category = "salon",
            Location = "Nairobi",
            MpesaNumber = "1",
            OwnerId = owner.Id,
            TimeZoneId = "UTC",
            CreatedAt = DateTime.UtcNow
        };
        var otherBusiness = new Business
        {
            Id = Guid.NewGuid(),
            Name = "Other Studio",
            Category = "salon",
            Location = "Nairobi",
            MpesaNumber = "1",
            OwnerId = otherOwner.Id,
            TimeZoneId = "UTC",
            CreatedAt = DateTime.UtcNow
        };
        ctx.Businesses.AddRange(business, otherBusiness);

        ctx.CustomerBusinessEnrollments.AddRange(
            new CustomerBusinessEnrollment { Id = Guid.NewGuid(), CustomerId = customer.Id, BusinessId = business.Id, Status = CustomerBusinessEnrollmentStatus.Active, CreatedAt = DateTime.UtcNow.AddDays(-60) },
            new CustomerBusinessEnrollment { Id = Guid.NewGuid(), CustomerId = otherCustomer.Id, BusinessId = business.Id, Status = CustomerBusinessEnrollmentStatus.Active, CreatedAt = DateTime.UtcNow.AddDays(-30) },
            new CustomerBusinessEnrollment { Id = Guid.NewGuid(), CustomerId = otherCustomer.Id, BusinessId = otherBusiness.Id, Status = CustomerBusinessEnrollmentStatus.Active, CreatedAt = DateTime.UtcNow.AddDays(-30) });

        var now = DateTime.UtcNow;

        // This customer: two settled payments (one earlier, one later), one refund, one
        // pending row and one voided row that must not count.
        ctx.FinancialTransactions.AddRange(
            Income(business.Id, customer.Id, 3000m, now.AddDays(-40)),
            Income(business.Id, customer.Id, 1200m, now.AddDays(-5)),
            Refund(business.Id, customer.Id, 500m, now.AddDays(-4)),
            Income(business.Id, customer.Id, 9000m, now.AddDays(-2), FinancialVerificationStatus.Pending),
            Income(business.Id, customer.Id, 7000m, now.AddDays(-1), FinancialVerificationStatus.Verified, voided: true),
            // Another customer at the same business — must not appear in this customer's totals.
            Income(business.Id, otherCustomer.Id, 4500m, now.AddDays(-3)),
            // The same customer at another business — must not appear under this business.
            Income(otherBusiness.Id, customer.Id, 8800m, now.AddDays(-3)));

        // Completed visits: two for Braids, one for Wash. Cancelled / no-show never count.
        var braids = Guid.NewGuid();
        var wash = Guid.NewGuid();
        ctx.Appointments.AddRange(
            AppointmentWithService(business.Id, customer.Id, braids, "Braids", AppointmentStatuses.Completed, -30),
            AppointmentWithService(business.Id, customer.Id, braids, "Braids", AppointmentStatuses.Completed, -10),
            AppointmentWithService(business.Id, customer.Id, wash, "Wash & Blow", AppointmentStatuses.Completed, -2),
            AppointmentWithService(business.Id, customer.Id, braids, "Braids", AppointmentStatuses.Cancelled, -1),
            AppointmentWithService(business.Id, customer.Id, braids, "Braids", AppointmentStatuses.NoShow, 0),
            AppointmentWithService(business.Id, otherCustomer.Id, braids, "Braids", AppointmentStatuses.Completed, -1));

        ctx.SaveChanges();

        var scope = new Mock<IBusinessScopeResolver>();
        scope.Setup(s => s.GetOwnedBusinessIdAsync(owner.Id)).ReturnsAsync(business.Id);

        var svc = new BusinessService(
            new UnitOfWork(ctx),
            ctx,
            new Mock<IInsightService>().Object,
            scope.Object,
            new Mock<ISubscriptionProvisioningService>().Object,
            new Mock<IModuleEntitlementService>().Object,
            new Mock<IBusinessSlugGenerator>().Object,
            new Mock<IBusinessMembershipResolver>().Object,
            TestHelpers.CreateLogger<BusinessService>());

        return new Env(ctx, svc, owner.Id, business, customer);
    }


    [Fact]
    public async Task Finance_Block_Counts_Only_Verified_Unvoided_Income_Less_Refunds()
    {
        var e = CreateEnv();

        var res = await e.Svc.GetCustomerDetailsAsync(e.OwnerId, e.Customer.Id);

        Assert.True(res.Success);
        var finance = res.Data!.Finance;

        // 3000 + 1200 recognised income − 500 refund. Pending and voided rows are excluded.
        Assert.Equal(3700m, finance.TotalSpent);
        Assert.Equal(2, finance.PaidTransactions);
        Assert.Equal(500m, finance.RefundedAmount);
        Assert.Equal(1850m, finance.AverageSpend);
        Assert.Equal("KES", finance.Currency);
    }

    [Fact]
    public async Task Finance_Block_Reports_Completed_Visits_And_Most_Served_Service()
    {
        var e = CreateEnv();

        var res = await e.Svc.GetCustomerDetailsAsync(e.OwnerId, e.Customer.Id);

        var finance = res.Data!.Finance;
        // Three completed appointments; the cancelled and no-show rows are not visits.
        Assert.Equal(3, finance.CompletedAppointments);
        Assert.NotNull(finance.TopService);
        Assert.Equal("Braids", finance.TopService!.ServiceName);
        Assert.Equal(2, finance.TopService.TimesServed);
        Assert.True(finance.HasHistory);
    }

    [Fact]
    public async Task Finance_Block_Reports_The_Most_Recent_Verified_Payment()
    {
        var e = CreateEnv();
        var expected = await e.Ctx.FinancialTransactions
            .Where(t => t.BusinessId == e.Business.Id
                        && t.CustomerId == e.Customer.Id
                        && t.Type == FinancialTransactionType.Income
                        && t.VerificationStatus == FinancialVerificationStatus.Verified
                        && !t.IsVoided)
            .MaxAsync(t => t.VerifiedAt);

        var res = await e.Svc.GetCustomerDetailsAsync(e.OwnerId, e.Customer.Id);

        Assert.Equal(expected, res.Data!.Finance.LastPaymentAt);
    }

    [Fact]
    public async Task Customer_Without_Financial_History_Gets_An_Empty_Block()
    {
        var e = CreateEnv();
        var freshCustomer = AddUser(e.Ctx, "fresh@t.com", "Fresh Customer", UserRole.Customer);
        e.Ctx.CustomerBusinessEnrollments.Add(new CustomerBusinessEnrollment
        {
            Id = Guid.NewGuid(),
            CustomerId = freshCustomer.Id,
            BusinessId = e.Business.Id,
            Status = CustomerBusinessEnrollmentStatus.Active,
            CreatedAt = DateTime.UtcNow
        });
        await e.Ctx.SaveChangesAsync();

        var res = await e.Svc.GetCustomerDetailsAsync(e.OwnerId, freshCustomer.Id);

        Assert.True(res.Success);
        var finance = res.Data!.Finance;
        Assert.False(finance.HasHistory);
        Assert.Equal(0m, finance.TotalSpent);
        Assert.Equal(0m, finance.AverageSpend);
        Assert.Equal(0, finance.PaidTransactions);
        Assert.Equal(0, finance.CompletedAppointments);
        Assert.Null(finance.TopService);
        Assert.Null(finance.LastPaymentAt);
    }


}
