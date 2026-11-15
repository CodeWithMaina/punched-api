using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using PunchedApi.Application.Authorization;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;
using PunchedApi.Application.Settings;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;
using PunchedApi.Infrastructure.Repositories;
using PunchedApi.Infrastructure.Services.Payments;

namespace PunchedApi.Tests;

/// <summary>
/// The payment stage of a booking: "pay after service" creates a real outstanding
/// payment, that row is what later collections settle, and nothing — least of all a
/// typed amount or a pasted SMS — is ever marked paid without the right decision.
/// </summary>
public class PayAfterServiceTests
{
    private const string MpesaMessage =
        "QJK42NUM01 Confirmed. Ksh1,000.00 sent to NURU STUDIO for account 12345 on 12/5/26 at 3:04 PM. " +
        "New M-PESA balance is Ksh500.00.";

    private static readonly DateTime Ten = new(2026, 3, 2, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Day = new(2026, 3, 2);

    private sealed class Env
    {
        public ApplicationDbContext Context = null!;
        public Business Business = null!;
        public User Owner = null!;
        public User Customer = null!;
        public User Staff = null!;
        public ServiceCatalogItem Service = null!;
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

    private static PaymentService Payments(ApplicationDbContext context)
    {
        var env = new Mock<IHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns("Development");
        return new PaymentService(
            context,
            new UnitOfWork(context),
            new IPaymentProvider[] { new CashPaymentProvider(NullLogger<CashPaymentProvider>.Instance) },
            new PermissionService(),
            new PaymentCredentialProtector(Options.Create(new PaymentOptions()), env.Object),
            Options.Create(new PaymentOptions()),
            BookingTestBase.CreateNotificationService(context),
            NullLogger<PaymentService>.Instance);
    }

    /// <summary>Owner + business + enrolled customer + staffed 1,000 KES service and manual M-PESA.</summary>
    private static async Task<Env> CreateEnvAsync(SqliteConnectionHolder holder)
    {
        var context = holder.Context;
        var owner = BookingTestBase.CreateOwner("pay-owner@test.com");
        var business = BookingTestBase.CreateBusiness(owner.Id, "Pay Studio");
        var customer = BookingTestBase.CreateCustomer("pay-customer@test.com");
        var staff = BookingTestBase.CreateStaff(business.Id, "pay-staff@test.com");
        var service = BookingTestBase.CreateService(business.Id, "Haircut", 60, 1000m);

        var config = new BusinessPaymentConfig
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            CashEnabled = true,
            MpesaEnabled = false,
            ManualMpesaEnabled = true,
            MpesaPaybillNumber = "247247",
            MpesaPaybillAccount = "12345",
            MpesaPayeeName = "NURU STUDIO",
            BookingFeePercentage = 50m,
            CreatedAt = DateTime.UtcNow
        };

        var enrollment = new CustomerBusinessEnrollment
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            BusinessId = business.Id,
            Status = CustomerBusinessEnrollmentStatus.Active,
            Source = "test",
            EnrolledAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await BookingTestBase.SeedAsync(context, owner, business, customer, staff, service,
            config, enrollment,
            BookingTestBase.CreateAssignment(business.Id, staff.Id, service.Id),
            BookingTestBase.CreateShift(business.Id, staff.Id, Day, 9, 18));

        return new Env
        {
            Context = context, Business = business, Owner = owner,
            Customer = customer, Staff = staff, Service = service
        };
    }

    /// <summary>Books through the owner wizard's on-behalf route, optionally "pay after service".</summary>
    private static Task<ApiResponse<AppointmentResponse>> BookAsync(Env env, bool payAfterService)
        => BookingTestBase.CreateAppointmentService(env.Context).CreateAppointmentOnBehalfAsync(
            env.Owner.Id, "Business", new CreateAppointmentOnBehalfRequest
            {
                BusinessId = env.Business.Id,
                ServiceIds = [env.Service.Id],
                StaffUserId = env.Staff.Id,
                ScheduledAt = Ten,
                CustomerId = env.Customer.Id,
                PayAfterService = payAfterService
            });

    private static async Task<Payment> ReloadAsync(ApplicationDbContext context, Guid paymentId)
    {
        context.ChangeTracker.Clear();
        return await context.Payments.Include(p => p.Attempts).SingleAsync(p => p.Id == paymentId);
    }

    // ═══════════════════════════════════════════════════════════
    //  PAY AFTER SERVICE
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task PayAfterService_CreatesAnUnpaidPaymentForTheAppointmentsFullValue()
    {
        using var holder = SqliteConnectionHolder.Create();
        var env = await CreateEnvAsync(holder);

        var booked = await BookAsync(env, payAfterService: true);

        Assert.True(booked.Success, booked.Error?.Message);

        var payment = await env.Context.Payments.SingleAsync();
        Assert.Equal(booked.Data!.Id, payment.AppointmentId);
        Assert.Equal(env.Customer.Id, payment.CustomerId);
        Assert.Equal(env.Business.Id, payment.BusinessId);
        Assert.Equal(1000m, payment.Amount);
        Assert.Equal(PaymentType.BalancePayment, payment.Type);
        // Unpaid, and honest about it: no channel has been chosen and nothing is settled.
        Assert.Equal(PaymentStatus.Created, payment.Status);
        Assert.Equal(PaymentMethod.Unspecified, payment.Method);
        Assert.False(PaymentStatuses.IsSettled(payment.Status));
        Assert.StartsWith("PMT-", payment.Reference);
    }

    [Fact]
    public async Task BookingWithoutPayAfterService_LeavesNothingOwed()
    {
        using var holder = SqliteConnectionHolder.Create();
        var env = await CreateEnvAsync(holder);

        var booked = await BookAsync(env, payAfterService: false);

        Assert.True(booked.Success, booked.Error?.Message);
        Assert.False(await env.Context.Payments.AnyAsync());
    }

    [Fact]
    public async Task OutstandingPayment_ShowsTheBalanceAsUnpaidAndNeverAsMoneyReceived()
    {
        using var holder = SqliteConnectionHolder.Create();
        var env = await CreateEnvAsync(holder);
        var booked = await BookAsync(env, payAfterService: true);
        var paymentId = (await env.Context.Payments.AsNoTracking().SingleAsync()).Id;

        var payments = Payments(env.Context);
        var summary = await payments.GetAppointmentPaymentSummaryAsync(
            booked.Data!.Id, env.Owner.Id, "Business");

        Assert.True(summary.Success, summary.Error?.Message);
        Assert.Equal(1000m, summary.Data!.ServiceTotal);
        Assert.Equal(0m, summary.Data.AmountPaid);
        Assert.Equal(1000m, summary.Data.Balance);
        Assert.Equal("unpaid", summary.Data.PaymentStatus);
        Assert.Equal("pending", summary.Data.VerificationStatus);
        Assert.Contains(summary.Data.Payments, p => p.Id == paymentId && p.Status == "created");
    }

    // ═══════════════════════════════════════════════════════════
    //  CASH COLLECTION
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task CollectingCash_SettlesTheOutstandingRowAndNamesItsChannel()
    {
        using var holder = SqliteConnectionHolder.Create();
        var env = await CreateEnvAsync(holder);
        var booked = await BookAsync(env, payAfterService: true);
        var outstanding = await env.Context.Payments.AsNoTracking().SingleAsync();

        var confirmed = await Payments(env.Context)
            .ConfirmCashAsync(outstanding.Id, "Taken at the desk", env.Owner.Id, "Business");

        Assert.True(confirmed.Success, confirmed.Error?.Message);
        Assert.Equal("success", confirmed.Data!.Status);

        var settled = await ReloadAsync(env.Context, outstanding.Id);
        Assert.Equal(PaymentStatus.Success, settled.Status);
        Assert.Equal(PaymentMethod.Cash, settled.Method);
        Assert.Equal(1000m, settled.Amount);
        // No second row: the money that arrived is the row that was owed.
        Assert.Single(await env.Context.Payments.ToListAsync());

        var summary = await Payments(env.Context)
            .GetAppointmentPaymentSummaryAsync(booked.Data!.Id, env.Owner.Id, "Business");
        Assert.Equal(1000m, summary.Data!.AmountPaid);
        Assert.Equal(0m, summary.Data.Balance);
        Assert.Equal("paid", summary.Data.PaymentStatus);
    }

    [Fact]
    public async Task CollectingLessCashThanOwed_LeavesTheRemainderOutstanding()
    {
        using var holder = SqliteConnectionHolder.Create();
        var env = await CreateEnvAsync(holder);
        var booked = await BookAsync(env, payAfterService: true);
        var outstanding = await env.Context.Payments.AsNoTracking().SingleAsync();

        var partial = await Payments(env.Context)
            .ConfirmCashAsync(outstanding.Id, "Part payment", env.Owner.Id, "Business", amount: 400m);

        Assert.True(partial.Success, partial.Error?.Message);
        Assert.Equal(400m, partial.Data!.Amount);

        var rows = await env.Context.Payments.AsNoTracking().OrderBy(p => p.CreatedAt).ToListAsync();
        Assert.Equal(2, rows.Count);
        var remainder = Assert.Single(rows.Where(p => p.Status == PaymentStatus.Created));
        Assert.Equal(600m, remainder.Amount);
        Assert.Equal(PaymentMethod.Unspecified, remainder.Method);
        // Settled + outstanding still adds up to the appointment's value.
        Assert.Equal(1000m, rows.Sum(p => p.Amount));

        var summary = await Payments(env.Context)
            .GetAppointmentPaymentSummaryAsync(booked.Data!.Id, env.Owner.Id, "Business");
        Assert.Equal(400m, summary.Data!.AmountPaid);
        Assert.Equal(600m, summary.Data.Balance);
        Assert.Equal("partially_paid", summary.Data.PaymentStatus);
    }

    [Fact]
    public async Task CollectingMoreCashThanOwed_IsRefused()
    {
        using var holder = SqliteConnectionHolder.Create();
        var env = await CreateEnvAsync(holder);
        await BookAsync(env, payAfterService: true);
        var outstanding = await env.Context.Payments.AsNoTracking().SingleAsync();

        var over = await Payments(env.Context)
            .ConfirmCashAsync(outstanding.Id, "Too much", env.Owner.Id, "Business", amount: 1500m);

        Assert.False(over.Success);
        Assert.Equal("VALIDATION_ERROR", over.Error?.Code);
        Assert.Equal(PaymentStatus.Created, (await ReloadAsync(env.Context, outstanding.Id)).Status);
    }

    // ═══════════════════════════════════════════════════════════
    //  M-PESA COLLECTION (manual channel: the owner decides)
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task MpesaOnTheOutstandingRow_NamesTheChannelAndWaitsForAVerification()
    {
        using var holder = SqliteConnectionHolder.Create();
        var env = await CreateEnvAsync(holder);
        await BookAsync(env, payAfterService: true);
        var outstanding = await env.Context.Payments.AsNoTracking().SingleAsync();

        var payments = Payments(env.Context);
        var submitted = await payments.SubmitMpesaMessageAsync(
            outstanding.Id, new SubmitMpesaMessageRequest { Message = MpesaMessage },
            env.Customer.Id, "Customer");

        Assert.True(submitted.Success, submitted.Error?.Message);
        // A pasted message is evidence, never proof — it must not settle anything.
        Assert.Equal("submitted", submitted.Data!.Status);

        var stored = await ReloadAsync(env.Context, outstanding.Id);
        Assert.Equal(PaymentMethod.Mpesa, stored.Method);
        Assert.Equal(PaymentProviderKind.ManualMpesa, stored.Provider);
        Assert.Equal("QJK42NUM01", stored.ParsedTransactionCode);
        Assert.False(PaymentStatuses.IsSettled(stored.Status));

        var verified = await payments.VerifyPaymentAsync(
            outstanding.Id, new VerifyPaymentRequest { Note = "Checked on the statement" },
            env.Owner.Id, "Business");

        Assert.True(verified.Success, verified.Error?.Message);
        Assert.Equal("verified", verified.Data!.Status);
        Assert.Equal(PaymentMethod.Mpesa, (await ReloadAsync(env.Context, outstanding.Id)).Method);
    }

    [Fact]
    public async Task FullPaymentByManualMpesa_RecordsThePhoneAndAmount_ButStaysUnpaid()
    {
        using var holder = SqliteConnectionHolder.Create();
        var env = await CreateEnvAsync(holder);
        var booked = await BookAsync(env, payAfterService: true);
        var outstanding = await env.Context.Payments.AsNoTracking().SingleAsync();

        var created = await Payments(env.Context).CreatePaymentAsync(
            new CreatePaymentRequest
            {
                AppointmentId = booked.Data!.Id,
                Method = "mpesa",
                Type = "full_payment",
                PhoneNumber = "0712345678",
                Amount = 1000m
            },
            env.Owner.Id, "Business");

        Assert.True(created.Success, created.Error?.Message);
        Assert.Equal("mpesa", created.Data!.Method);
        Assert.Equal(1000m, created.Data.Amount);
        Assert.Equal("254712345678", created.Data.PhoneNumber);
        // Typing a number and an amount is not a transaction: the row is not paid.
        Assert.Equal("created", created.Data.Status);

        // …and it reused the row that was already owed rather than inventing a second one.
        Assert.Equal(outstanding.Id, created.Data.Id);
        Assert.Single(await env.Context.Payments.ToListAsync());
    }

    [Fact]
    public async Task CreatingAPaymentWhileOneAwaitsVerification_IsRefused()
    {
        using var holder = SqliteConnectionHolder.Create();
        var env = await CreateEnvAsync(holder);
        var booked = await BookAsync(env, payAfterService: true);
        var outstanding = await env.Context.Payments.AsNoTracking().SingleAsync();

        var payments = Payments(env.Context);
        var submitted = await payments.SubmitMpesaMessageAsync(
            outstanding.Id, new SubmitMpesaMessageRequest { Message = MpesaMessage },
            env.Customer.Id, "Customer");
        Assert.True(submitted.Success, submitted.Error?.Message);

        env.Context.ChangeTracker.Clear();
        var duplicate = await payments.CreatePaymentAsync(
            new CreatePaymentRequest { AppointmentId = booked.Data!.Id, Method = "mpesa", Type = "balance_payment" },
            env.Owner.Id, "Business");

        Assert.False(duplicate.Success);
        Assert.Equal("DUPLICATE_PAYMENT", duplicate.Error?.Code);
        Assert.Single(await env.Context.Payments.ToListAsync());
    }

    [Fact]
    public async Task BookingFeeCannotBeCollectedTwice()
    {
        using var holder = SqliteConnectionHolder.Create();
        var env = await CreateEnvAsync(holder);
        var booked = await BookAsync(env, payAfterService: false);
        var payments = Payments(env.Context);

        // A 50% booking fee on a 1,000 KES service.
        var first = await payments.CreatePaymentAsync(
            new CreatePaymentRequest
            {
                AppointmentId = booked.Data!.Id, Method = "mpesa", Type = "booking_fee",
                PhoneNumber = "0712345678"
            },
            env.Owner.Id, "Business");
        Assert.True(first.Success, first.Error?.Message);
        Assert.Equal(500m, first.Data!.Amount);

        env.Context.ChangeTracker.Clear();
        var second = await payments.CreatePaymentAsync(
            new CreatePaymentRequest
            {
                AppointmentId = booked.Data.Id, Method = "mpesa", Type = "booking_fee",
                PhoneNumber = "0712345678"
            },
            env.Owner.Id, "Business");

        Assert.False(second.Success);
        Assert.Equal("DUPLICATE_PAYMENT", second.Error?.Code);
        Assert.Single(await env.Context.Payments.ToListAsync());
    }

    [Fact]
    public async Task BalanceAfterAVerifiedBookingFee_IsTheRemainderNotTheFullTotal()
    {
        using var holder = SqliteConnectionHolder.Create();
        var env = await CreateEnvAsync(holder);
        var booked = await BookAsync(env, payAfterService: false);
        var payments = Payments(env.Context);

        var fee = await payments.CreatePaymentAsync(
            new CreatePaymentRequest
            {
                AppointmentId = booked.Data!.Id, Method = "mpesa", Type = "booking_fee",
                PhoneNumber = "0712345678"
            },
            env.Owner.Id, "Business");
        Assert.True(fee.Success, fee.Error?.Message);

        env.Context.ChangeTracker.Clear();
        var submitted = await payments.SubmitMpesaMessageAsync(
            fee.Data!.Id,
            new SubmitMpesaMessageRequest
            {
                Message = "QJK42NUM02 Confirmed. Ksh500.00 sent to NURU STUDIO for account 12345 on 12/5/26 at 3:04 PM. New M-PESA balance is Ksh500.00."
            },
            env.Owner.Id, "Business");
        Assert.True(submitted.Success, submitted.Error?.Message);
        Assert.True((await payments.VerifyPaymentAsync(
            fee.Data.Id, new VerifyPaymentRequest(), env.Owner.Id, "Business")).Success);

        env.Context.ChangeTracker.Clear();
        var balance = await payments.CreatePaymentAsync(
            new CreatePaymentRequest { AppointmentId = booked.Data.Id, Method = "cash", Type = "balance_payment" },
            env.Owner.Id, "Business");

        Assert.True(balance.Success, balance.Error?.Message);
        Assert.Equal(500m, balance.Data!.Amount);
    }

    [Fact]
    public async Task PartialCollectionThroughANewPayment_LeavesTheRestOutstanding()
    {
        using var holder = SqliteConnectionHolder.Create();
        var env = await CreateEnvAsync(holder);
        var booked = await BookAsync(env, payAfterService: true);
        var payments = Payments(env.Context);

        // The appointment owes 1,000; only 400 is taken at the desk.
        var created = await payments.CreatePaymentAsync(
            new CreatePaymentRequest
            {
                AppointmentId = booked.Data!.Id, Method = "cash", Type = "balance_payment", Amount = 400m
            },
            env.Owner.Id, "Business");
        Assert.True(created.Success, created.Error?.Message);
        Assert.Equal(400m, created.Data!.Amount);

        Assert.True((await payments.ConfirmCashAsync(
            created.Data.Id, "Part payment", env.Owner.Id, "Business")).Success);

        env.Context.ChangeTracker.Clear();
        var rows = await env.Context.Payments.AsNoTracking().ToListAsync();
        Assert.Equal(2, rows.Count);
        // What is still owed is its own row, so nothing silently disappears.
        Assert.Equal(600m, rows.Single(p => p.Status == PaymentStatus.Created).Amount);
        Assert.Equal(1000m, rows.Sum(p => p.Amount));

        var summary = await payments.GetAppointmentPaymentSummaryAsync(
            booked.Data.Id, env.Owner.Id, "Business");
        Assert.Equal(400m, summary.Data!.AmountPaid);
        Assert.Equal(600m, summary.Data.Balance);
    }
}
