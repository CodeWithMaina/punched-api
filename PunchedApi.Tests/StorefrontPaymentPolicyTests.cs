using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Tests;

/// <summary>
/// The storefront's booking policy: which payment options a business offers, and whether
/// the server enforces that choice when a customer books.
///
/// <para><b>Why this matters.</b> The storefront hides controls it should not show, but a
/// hidden control is not a rule — anyone can post to <c>POST /v1/appointments</c>. Every
/// test here therefore asserts the SERVER's answer to a request a tampering client could
/// send, and that a refused booking leaves nothing behind: no appointment, and no payment
/// record for an appointment that does not exist.</para>
/// </summary>
public class StorefrontPaymentPolicyTests
{
    // Inside the seeded shift, and far enough ahead that the customer path's lead-time rule
    // (which the storefront is subject to) is satisfied whatever day the suite runs on.
    private static readonly DateTime Ten = DateTime.UtcNow.Date.AddDays(7).AddHours(10);
    private static readonly DateOnly Day = DateOnly.FromDateTime(Ten);

    private sealed class Env
    {
        public ApplicationDbContext Context = null!;
        public Business Business = null!;
        public User Owner = null!;
        public User Customer = null!;
        public User Staff = null!;
        public ServiceCatalogItem Service = null!;
        public BusinessPaymentConfig Config = null!;
    }

    private sealed class SqliteConnectionHolder : IDisposable
    {
        public Microsoft.Data.Sqlite.SqliteConnection Connection = null!;
        public ApplicationDbContext Context = null!;

        public static SqliteConnectionHolder Create()
        {
            var connection = BookingTestBase.CreateConnection();
            return new SqliteConnectionHolder
            {
                Connection = connection,
                Context = BookingTestBase.CreateContext(connection)
            };
        }

        public void Dispose()
        {
            Context.Dispose();
            Connection.Dispose();
        }
    }

    /// <summary>
    /// One business with an enrolled customer, a staffed 1,000 KES service and a payment
    /// configuration whose two storefront options are given explicitly.
    /// </summary>
    private static async Task<Env> CreateEnvAsync(
        SqliteConnectionHolder holder, bool payUpfront, bool payAfterService,
        CustomerType customerType = CustomerType.Registered)
    {
        var context = holder.Context;
        var owner = BookingTestBase.CreateOwner("policy-owner@test.com");
        var business = BookingTestBase.CreateBusiness(owner.Id, "Policy Studio");
        var customer = BookingTestBase.CreateCustomer("policy-customer@test.com");
        var staff = BookingTestBase.CreateStaff(business.Id, "policy-staff@test.com");
        var service = BookingTestBase.CreateService(business.Id, "Haircut", 60, 1000m);

        var config = new BusinessPaymentConfig
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            CreatedAt = DateTime.UtcNow,
            CashEnabled = true,
            PayUpfrontEnabled = payUpfront,
            PayAfterServiceEnabled = payAfterService
        };

        var enrollment = new CustomerBusinessEnrollment
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            BusinessId = business.Id,
            Status = CustomerBusinessEnrollmentStatus.Active,
            Source = "test",
            // Guest or Registered: a verified guest books with a short-lived token and must
            // stay a guest, so the fixture has to be able to seed either relationship.
            Type = customerType,
            EnrolledAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await BookingTestBase.SeedAsync(context, owner, business, customer, staff, service,
            config, enrollment,
            BookingTestBase.CreateAssignment(business.Id, staff.Id, service.Id),
            BookingTestBase.CreateShift(business.Id, staff.Id, Day, 9, 18));

        return new Env
        {
            Context = context, Business = business, Owner = owner, Customer = customer,
            Staff = staff, Service = service, Config = config
        };
    }

    /// <summary>
    /// Books the way the storefront does: the policy is resolved by the same service method the
    /// public booking path calls (<c>AppointmentService.CreateAppointmentAsync</c>), exercised here
    /// directly so the policy can be tested without the HTTP layer.
    /// </summary>
    private static Task<ApiResponse<AppointmentResponse>> BookAsync(
        Env env, string? paymentOption, bool isGuest = false)
        => BookingTestBase.CreateAppointmentService(env.Context).CreateAppointmentAsync(
            env.Customer.Id, "Customer", new CreateAppointmentRequest
            {
                BusinessId = env.Business.Id,
                ServiceIds = [env.Service.Id],
                StaffUserId = env.Staff.Id,
                ScheduledAt = Ten,
                PaymentOption = paymentOption
            },
            isGuest);

    private static Task<List<Payment>> PaymentsAsync(Env env) =>
        env.Context.Payments.AsNoTracking().OrderBy(p => p.CreatedAt).ToListAsync();

    // ═══════════════════════════════════════════════════════════
    //  THE MATRIX
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task BothEnabled_PayingDuringBooking_IsBookedWithNothingOutstanding()
    {
        using var holder = SqliteConnectionHolder.Create();
        var env = await CreateEnvAsync(holder, payUpfront: true, payAfterService: true);

        var result = await BookAsync(env, StorefrontPaymentOptions.PayUpfront);

        Assert.True(result.Success, result.Error?.Message);
        // Paying during booking goes through the payment flow, so the appointment does not
        // carry an outstanding balance of its own.
        Assert.Empty(await PaymentsAsync(env));
    }

    [Fact]
    public async Task BothEnabled_PayingAfterService_WritesTheOutstandingPaymentInTheSameTransaction()
    {
        using var holder = SqliteConnectionHolder.Create();
        var env = await CreateEnvAsync(holder, payUpfront: true, payAfterService: true);

        var result = await BookAsync(env, StorefrontPaymentOptions.PayAfterService);

        Assert.True(result.Success, result.Error?.Message);
        var payment = Assert.Single(await PaymentsAsync(env));
        Assert.Equal(result.Data!.Id, payment.AppointmentId);
        Assert.Equal(env.Customer.Id, payment.CustomerId);
        Assert.Equal(env.Business.Id, payment.BusinessId);
        Assert.Equal(1000m, payment.Amount);
        Assert.Equal(PaymentStatus.Created, payment.Status);
        // The channel is decided when the money is actually taken, never before.
        Assert.Equal(PaymentMethod.Unspecified, payment.Method);
        Assert.False(PaymentStatuses.IsSettled(payment.Status));
    }

    [Fact]
    public async Task BothEnabled_WithoutAChoice_IsRefusedAndBooksNothing()
    {
        using var holder = SqliteConnectionHolder.Create();
        var env = await CreateEnvAsync(holder, payUpfront: true, payAfterService: true);

        var result = await BookAsync(env, paymentOption: null);

        Assert.False(result.Success);
        Assert.Equal("PAYMENT_OPTION_REQUIRED", result.Error?.Code);
        Assert.Empty(await env.Context.Appointments.ToListAsync());
        Assert.Empty(await PaymentsAsync(env));
    }

    [Fact]
    public async Task OnlyPayingDuringBooking_AChoiceIsNotRequired()
    {
        using var holder = SqliteConnectionHolder.Create();
        var env = await CreateEnvAsync(holder, payUpfront: true, payAfterService: false);

        var result = await BookAsync(env, paymentOption: null);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Empty(await PaymentsAsync(env));
    }

    [Fact]
    public async Task OnlyPayingAfterService_AChoiceIsNotRequiredAndTheBalanceIsRecorded()
    {
        using var holder = SqliteConnectionHolder.Create();
        var env = await CreateEnvAsync(holder, payUpfront: false, payAfterService: true);

        var result = await BookAsync(env, paymentOption: null);

        Assert.True(result.Success, result.Error?.Message);
        var payment = Assert.Single(await PaymentsAsync(env));
        Assert.Equal(result.Data!.Id, payment.AppointmentId);
        Assert.Equal(1000m, payment.Amount);
    }


    [Theory]
    [InlineData(true, false, "pay_after_service")]   // pay-after switched off
    [InlineData(false, true, "pay_upfront")]         // paying during booking switched off
    public async Task AChoiceTheBusinessSwitchedOff_IsRefused(
        bool payUpfront, bool payAfterService, string submitted)
    {
        using var holder = SqliteConnectionHolder.Create();
        var env = await CreateEnvAsync(holder, payUpfront, payAfterService);

        var result = await BookAsync(env, submitted);

        Assert.False(result.Success);
        Assert.Equal("PAYMENT_OPTION_DISABLED", result.Error?.Code);
        Assert.Empty(await env.Context.Appointments.ToListAsync());
        Assert.Empty(await PaymentsAsync(env));
    }

    [Theory]
    [InlineData("pay_upfront")]
    [InlineData("pay_after_service")]
    public async Task BothSwitchedOff_RefusesEveryBooking(string submitted)
    {
        using var holder = SqliteConnectionHolder.Create();
        var env = await CreateEnvAsync(holder, payUpfront: false, payAfterService: false);

        var result = await BookAsync(env, submitted);

        Assert.False(result.Success);
        Assert.Equal("BOOKING_UNAVAILABLE", result.Error?.Code);
        // Nothing was created: not an appointment, and no payment for one.
        Assert.Empty(await env.Context.Appointments.ToListAsync());
        Assert.Empty(await PaymentsAsync(env));
    }

    [Fact]
    public async Task NoConfigurationRow_BehavesAsTheDefaultsPayingDuringBooking()
    {
        using var holder = SqliteConnectionHolder.Create();
        var env = await CreateEnvAsync(holder, payUpfront: true, payAfterService: false);
        // A business that has never opened its payment settings has no row at all.
        env.Context.BusinessPaymentConfigs.Remove(env.Config);
        await env.Context.SaveChangesAsync();

        var result = await BookAsync(env, paymentOption: null);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Empty(await PaymentsAsync(env));

        // Pay-after-service is not offered by default, so asking for it is still refused.
        var asked = await BookAsync(env, StorefrontPaymentOptions.PayAfterService);
        Assert.False(asked.Success);
        Assert.Equal("PAYMENT_OPTION_DISABLED", asked.Error?.Code);
    }

    [Fact]
    public async Task AnUnknownOptionValue_IsRejectedRatherThanGuessed()
    {
        using var holder = SqliteConnectionHolder.Create();
        var env = await CreateEnvAsync(holder, payUpfront: true, payAfterService: true);

        var result = await BookAsync(env, "pay_whenever");

        Assert.False(result.Success);
        Assert.Equal("VALIDATION_ERROR", result.Error?.Code);
        Assert.Empty(await env.Context.Appointments.ToListAsync());
    }

    [Fact]
    public async Task GuestCustomer_PayingAfterService_IsAcceptedAndKeepsTheCustomerAGuest()
    {
        using var holder = SqliteConnectionHolder.Create();
        var env = await CreateEnvAsync(holder, payUpfront: false, payAfterService: true,
            customerType: CustomerType.Guest);

        var result = await BookAsync(env, StorefrontPaymentOptions.PayAfterService, isGuest: true);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Single(await PaymentsAsync(env));

        // A guest stays a guest until they sign up — the storefront must not require an
        // account, and booking must not silently promote the relationship.
        var enrollment = await env.Context.CustomerBusinessEnrollments
            .AsNoTracking().SingleAsync(e => e.CustomerId == env.Customer.Id);
        Assert.Equal(CustomerType.Guest, enrollment.Type);
    }

    [Fact]
    public async Task APolicyChangeBeforeSubmit_IsEnforcedAgainstTheStaleClient()
    {
        using var holder = SqliteConnectionHolder.Create();
        var env = await CreateEnvAsync(holder, payUpfront: true, payAfterService: false);

        // The customer opened the page when paying during booking was the only option.
        Assert.Equal("pay_upfront", StorefrontPaymentOptions.Resolve(true, false, null).Option);

        // …then the owner switched the policy to pay-after-service only.
        env.Config.PayUpfrontEnabled = false;
        env.Config.PayAfterServiceEnabled = true;
        await env.Context.SaveChangesAsync();

        // The request the stale page sends is refused: the server re-reads the policy.
        var refused = await BookAsync(env, StorefrontPaymentOptions.PayUpfront);
        Assert.False(refused.Success);
        Assert.Equal("PAYMENT_OPTION_DISABLED", refused.Error?.Code);
        Assert.Empty(await env.Context.Appointments.ToListAsync());

        // The newly offered option works, and records the balance.
        var allowed = await BookAsync(env, StorefrontPaymentOptions.PayAfterService);
        Assert.True(allowed.Success, allowed.Error?.Message);
        Assert.Single(await PaymentsAsync(env));
    }

    [Fact]
    public async Task Tenancy_EachBusinessEnforcesItsOwnPolicy()
    {
        using var holder = SqliteConnectionHolder.Create();
        var env = await CreateEnvAsync(holder, payUpfront: true, payAfterService: false);

        // A second business with the opposite policy.
        var otherOwner = BookingTestBase.CreateOwner("other-policy-owner@test.com");
        var otherBusiness = BookingTestBase.CreateBusiness(otherOwner.Id, "Other Policy Studio");
        var otherCustomer = BookingTestBase.CreateCustomer("other-policy-customer@test.com");
        var otherService = BookingTestBase.CreateService(otherBusiness.Id, "Cut", 60, 800m);
        await BookingTestBase.SeedAsync(env.Context, otherOwner, otherBusiness, otherCustomer, otherService,
            new BusinessPaymentConfig
            {
                Id = Guid.NewGuid(), BusinessId = otherBusiness.Id, CreatedAt = DateTime.UtcNow,
                PayUpfrontEnabled = false, PayAfterServiceEnabled = true
            },
            new CustomerBusinessEnrollment
            {
                Id = Guid.NewGuid(), CustomerId = otherCustomer.Id, BusinessId = otherBusiness.Id,
                Status = CustomerBusinessEnrollmentStatus.Active, Source = "test",
                EnrolledAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });

        // Business A: paying during booking is fine, pay-after is not.
        Assert.True((await BookAsync(env, StorefrontPaymentOptions.PayUpfront)).Success);
        var refusedForA = await BookAsync(env, StorefrontPaymentOptions.PayAfterService);
        Assert.Equal("PAYMENT_OPTION_DISABLED", refusedForA.Error?.Code);

        // A customer of the other business cannot book here on terms this business refused.
        var crossBusiness = await BookingTestBase.CreateAppointmentService(env.Context)
            .CreateAppointmentAsync(otherCustomer.Id, "Customer", new CreateAppointmentRequest
            {
                BusinessId = env.Business.Id,
                ServiceIds = [env.Service.Id],
                ScheduledAt = Ten.AddDays(1),
                PaymentOption = StorefrontPaymentOptions.PayAfterService
            });
        Assert.False(crossBusiness.Success);

        // The other business's own policy applies to its own booking, and its payment
        // carries its own business id.
        var allowedForB = await BookingTestBase.CreateAppointmentService(env.Context)
            .CreateAppointmentAsync(otherCustomer.Id, "Customer", new CreateAppointmentRequest
            {
                BusinessId = otherBusiness.Id,
                ServiceIds = [otherService.Id],
                ScheduledAt = Ten.AddDays(1),
                PaymentOption = StorefrontPaymentOptions.PayAfterService
            });
        Assert.True(allowedForB.Success, allowedForB.Error?.Message);

        var payments = await PaymentsAsync(env);
        Assert.Single(payments);
        Assert.Equal(otherBusiness.Id, payments[0].BusinessId);
    }
}


