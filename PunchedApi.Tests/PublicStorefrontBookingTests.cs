using Microsoft.Data.Sqlite;
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
/// The public storefront booking journey, end to end at the service boundary — run twice: once as a
/// visitor with no account at all, and once as a customer who is signed in but belongs to a
/// different business.
///
/// <para><b>The two things being proved.</b> First, that <i>who the caller is</i> cannot stop a
/// booking: no account, an account at another business, and the business's own owner are all served
/// the same way. Second, that <i>whether money has been verified</i> cannot stop a booking: the
/// appointment is booked, appears in the business's calendar, and a pasted M-PESA confirmation
/// waits in the payments module for the owner — never gating the booking, and never counted as
/// money received until it is verified.</para>
/// </summary>
public class PublicStorefrontBookingTests
{
    // A slot the customer-path lead-time rule accepts whatever day the suite runs on.
    private static readonly DateTime Slot = DateTime.UtcNow.Date.AddDays(7).AddHours(10);
    private static readonly DateOnly SlotDay = DateOnly.FromDateTime(Slot);

    /// <summary>A pasted M-PESA confirmation the parser accepts, for the configured 450 fee.</summary>
    private const string ValidMessage =
        "QJK42NUM01 Confirmed. Ksh450.00 sent to NURU STUDIO 247247 on 2/3/26 at 10:15 AM. "
        + "New M-PESA balance is Ksh1,200.00. Transaction cost, Ksh0.00.";

    private sealed class Env : IDisposable
    {
        /// <summary>Null when the env runs on the in-memory provider, which needs no connection.</summary>
        public SqliteConnection? Connection = null!;
        public ApplicationDbContext Context = null!;
        public PublicBookingService Public = null!;
        public BookingHoldService Holds = null!;
        public AppointmentService Appointments = null!;
        public PaymentService Payments = null!;
        public Business Business = null!;
        public User Owner = null!;
        public User Staff = null!;
        public ServiceCatalogItem Service = null!;

        public void Dispose()
        {
            Context.Dispose();
            Connection?.Dispose();
        }
    }

    private static PaymentCredentialProtector Protector()
    {
        var env = new Mock<IHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns("Development");
        return new PaymentCredentialProtector(Options.Create(new PaymentOptions()), env.Object);
    }

    private static PaymentService PaymentServiceFor(ApplicationDbContext context)
        => new(
            context,
            new UnitOfWork(context),
            new IPaymentProvider[] { new CashPaymentProvider(NullLogger<CashPaymentProvider>.Instance) },
            new PermissionService(),
            Protector(),
            Options.Create(new PaymentOptions()),
            BookingTestBase.CreateNotificationService(context),
            NullLogger<PaymentService>.Instance);

    /// <summary>
    /// One business that asks for 50% up front through manual M-PESA — the configuration that makes
    /// the slot hold and the M-PESA confirmation mandatory — with a staffed, bookable slot.
    /// </summary>
    private static async Task<Env> CreateEnvAsync(bool manualMpesa = true)
    {
        var connection = BookingTestBase.CreateConnection();
        var context = BookingTestBase.CreateContext(connection);

        var owner = BookingTestBase.CreateOwner($"storefront-owner-{Guid.NewGuid():N}@test.com");
        var business = BookingTestBase.CreateBusiness(owner.Id, "Nuru Studio");
        var staff = BookingTestBase.CreateStaff(business.Id, $"storefront-staff-{Guid.NewGuid():N}@test.com");
        var service = BookingTestBase.CreateService(business.Id, "Deluxe Fade", 45, 900m);

        var config = new BusinessPaymentConfig
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            CreatedAt = DateTime.UtcNow,
            CashEnabled = true,
            ManualMpesaEnabled = manualMpesa,
            MpesaAccountType = ManualMpesaMethods.Paybill,
            MpesaPaybillNumber = "247247",
            MpesaPaybillAccount = "NURU-1",
            MpesaPayeeName = "NURU STUDIO",
            BookingFeePercentage = 50m,
            PayUpfrontEnabled = true,
            PayAfterServiceEnabled = false,
        };

        await BookingTestBase.SeedAsync(context, owner, business, staff, service, config,
            BookingTestBase.CreateAssignment(business.Id, staff.Id, service.Id),
            BookingTestBase.CreateShift(business.Id, staff.Id, SlotDay, 8, 20));

        var modules = new Mock<IModuleEntitlementService>();
        modules.Setup(m => m.IsModuleEnabledAsync(It.IsAny<Guid>(), It.IsAny<string>())).ReturnsAsync(true);

        var appointments = BookingTestBase.CreateAppointmentService(context);
        var payments = PaymentServiceFor(context);

        var availability = BookingTestBase.CreateAvailabilityService(context);
        var readContexts = new BookingReadContextLoader(context);
        var holds = new BookingHoldService(
            context, availability, readContexts, TestHelpers.CreateLogger<BookingHoldService>());

        return new Env
        {
            Connection = connection,
            Context = context,
            Appointments = appointments,
            Payments = payments,
            Holds = holds,
            Public = new PublicBookingService(
                context, appointments, payments, modules.Object,
                TestHelpers.CreateLogger<PublicBookingService>(),
                holds: holds,
                tenant: null,
                enrollments: new CustomerEnrollmentService(
                    new UnitOfWork(context), context, NullLogger<CustomerEnrollmentService>.Instance)),
            Business = business,
            Owner = owner,
            Staff = staff,
            Service = service,
        };
    }

    /// <summary>A booking request exactly as the storefront would send it.</summary>
    private static PublicBookingRequest Request(Env env, string phone, string email,
        string name = "Wanjiru Kariuki", string? mpesaMessage = null, string? idempotencyKey = null,
        DateTime? scheduledAt = null) =>
        new()
        {
            BusinessId = env.Business.Id,
            ServiceIds = [env.Service.Id],
            StaffUserId = env.Staff.Id,
            ScheduledAt = scheduledAt ?? Slot,
            PaymentOption = StorefrontPaymentOptions.PayUpfront,
            FullName = name,
            PhoneNumber = phone,
            Email = email,
            MpesaMessage = mpesaMessage,
            IdempotencyKey = idempotencyKey,
        };

    private static Task<ApiResponse<PublicBookingResponse>> BookAsync(
        Env env, PublicBookingRequest request, Guid? callerUserId = null, string? callerRole = null,
        Guid? tenant = null)
        => env.Public.BookAsync(callerUserId, callerRole, tenant, request);

    /// <summary>
    /// A booking request that has done what a real customer must: held the slot and pasted the
    /// M-PESA confirmation.
    ///
    /// <para>The business charges a 50% fee, so both are mandatory — the server refuses a booking
    /// without a live hold or without a message. Every scenario below therefore starts from a hold,
    /// which is also what proves the hold is convertible.</para>
    /// </summary>
    private static async Task<PublicBookingRequest> RequestWithHoldAsync(
        Env env,
        string phone,
        string email,
        string name = "Wanjiru Kariuki",
        string? mpesaMessage = ValidMessage,
        string? idempotencyKey = null,
        DateTime? scheduledAt = null,
        string? paymentOption = null)
    {
        var request = Request(env, phone, email, name, mpesaMessage, idempotencyKey, scheduledAt);
        if (paymentOption != null) request.PaymentOption = paymentOption;

        var hold = await env.Holds.CreateAsync(
            env.Business.Id,
            new CreateBookingHoldRequest
            {
                ServiceIds = request.ServiceIds,
                StaffUserId = request.StaffUserId,
                ScheduledAt = request.ScheduledAt,
                IdempotencyKey = idempotencyKey,
                PaymentOption = request.PaymentOption,
                FullName = name,
                PhoneNumber = phone,
                Email = email,
            },
            tenantBusinessId: null,
            configuredHoldMinutes: null);

        // A retried intent reuses the hold it already created: the first booking consumed it, but
        // the customer's retry is the same attempt, so the same claim travels with it.
        if (!hold.Success)
        {
            env.Context.ChangeTracker.Clear();
            var existing = await env.Context.AppointmentSlotHolds.AsNoTracking()
                .Where(h => h.BusinessId == env.Business.Id
                    && (idempotencyKey == null || h.IdempotencyKey == idempotencyKey))
                .OrderByDescending(h => h.CreatedAt)
                .FirstOrDefaultAsync();

            Assert.True(existing != null, hold.Error?.Message);
            request.HoldId = existing!.Id;
            return request;
        }

        request.HoldId = hold.Data!.HoldId;
        return request;
    }

    /// <summary>The owner's appointment list, as the dashboard shows it.</summary>
    private static Task<ApiResponse<AppointmentDashboardResponse>> DashboardAsync(Env env) =>
        env.Appointments.GetAppointmentDashboardAsync(
            env.Owner.Id, null, null, null, null, null, null, null, "scheduledAt", "asc", 1, 50);

    // ═══════════════════════════════════════════════════════════
    //  SCENARIO 1 — a visitor with no account at all
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task NoAccount_BooksAndThePaymentWaitsForTheOwner()
    {
        using var env = await CreateEnvAsync();

        var booked = await BookAsync(
            env, await RequestWithHoldAsync(env, "0712345678", "wanjiru@example.test", mpesaMessage: ValidMessage));

        Assert.True(booked.Success, booked.Error?.Message);
        var appointment = booked.Data!.Appointment;

        // The appointment exists and is PENDING PAYMENT: the fee has been submitted but nobody has
        // verified it, so the booking is not confirmed yet. Verification is what confirms it.
        Assert.Equal(AppointmentStatuses.PendingPayment, appointment.Status);
        Assert.Equal(env.Business.Id, appointment.BusinessId);

        // The customer record belongs to THIS business and is tagged Guest.
        env.Context.ChangeTracker.Clear();
        var customerId = appointment.CustomerId;
        var enrollment = await env.Context.CustomerBusinessEnrollments
            .SingleAsync(e => e.CustomerId == customerId);
        Assert.Equal(env.Business.Id, enrollment.BusinessId);
        Assert.Equal(CustomerType.Guest, enrollment.Type);

        var customer = await env.Context.Users.FindAsync(customerId);
        Assert.Equal("Wanjiru Kariuki", customer!.FullName);
        Assert.Equal("0712345678", customer.PhoneNumber);
        Assert.Equal("wanjiru@example.test", customer.Email);

        // The payment is in the module, awaiting verification — with the receipt and amount the
        // server parsed out of the paste as its evidence.
        Assert.NotNull(booked.Data.Payment);
        Assert.Equal(PublicPaymentStates.PendingVerification, booked.Data.PaymentState);

        var payment = await env.Context.Payments.AsNoTracking()
            .SingleAsync(p => p.Id == booked.Data.Payment!.Id);
        Assert.Equal(PaymentStatus.Submitted, payment.Status);
        Assert.Equal(PaymentMethod.Mpesa, payment.Method);
        Assert.Equal(PaymentType.BookingFee, payment.Type);
        Assert.Equal(env.Business.Id, payment.BusinessId);
        Assert.Equal(450m, payment.ParsedAmount);
        Assert.Equal("QJK42NUM01", payment.ParsedTransactionCode);
        Assert.Equal(appointment.Id, payment.AppointmentId);

        // The business sees the booking immediately.
        var dashboard = await DashboardAsync(env);
        Assert.True(dashboard.Success, dashboard.Error?.Message);
        Assert.Contains(dashboard.Data!.Appointments, a => a.Id == appointment.Id);
    }

    [Fact]
    public async Task NoAccount_PendingPaymentIsNotRevenueUntilItIsVerified()
    {
        using var env = await CreateEnvAsync();

        var booked = await BookAsync(
            env, await RequestWithHoldAsync(env, "0712345678", "wanjiru@example.test", mpesaMessage: ValidMessage));
        Assert.True(booked.Success, booked.Error?.Message);

        // The money the dashboard counts as received is exactly the SETTLED payments — the rule the
        // payments module aggregates by (success or owner-verified), applied to the same period.
        // A submitted confirmation is a claim, so it is not in that set.
        Assert.Equal(0m, await CollectedTodayAsync(env));
        Assert.False(PaymentStatuses.IsSettled(
            Enum.Parse<PaymentStatus>(booked.Data!.Payment!.Status, true)));
        Assert.True(PaymentStatuses.IsAwaitingVerification(
            Enum.Parse<PaymentStatus>(booked.Data.Payment.Status, true)));

        // It is, however, visible where it should be: the owner's verification queue.
        var queue = await env.Payments.GetVerificationQueueAsync(
            new PaymentVerificationQuery { Status = "submitted" }, env.Owner.Id, "Business");
        Assert.True(queue.Success, queue.Error?.Message);
        Assert.Contains(queue.Data!.Items, item => item.PaymentId == booked.Data.Payment.Id);

        // The owner verifies the transaction.
        var verified = await env.Payments.VerifyPaymentAsync(
            booked.Data.Payment.Id,
            new VerifyPaymentRequest { Note = "Matches the M-PESA statement." },
            env.Owner.Id, "Business");
        Assert.True(verified.Success, verified.Error?.Message);
        Assert.Equal("verified", verified.Data!.Status);

        // The appointment is a live booking either way. Verifying the fee promotes Booked →
        // Confirmed — the business has confirmed the booking by accepting the money — and what must
        // never happen is that the payment decision cancels or re-creates the visit.
        env.Context.ChangeTracker.Clear();
        var unchanged = await env.Context.Appointments.FindAsync(booked.Data.Appointment.Id);
        Assert.Contains(unchanged!.Status, new[] { AppointmentStatuses.Booked, AppointmentStatuses.Confirmed });

        // And now it counts as money received.
        Assert.Equal(450m, await CollectedTodayAsync(env));
        Assert.False(await env.Context.Payments.AsNoTracking()
            .AnyAsync(p => p.Id == booked.Data.Payment.Id
                && (p.Status == PaymentStatus.Submitted || p.Status == PaymentStatus.UnderReview)));

        // The decision is auditable.
        var events = await env.Context.PaymentVerificationEvents.AsNoTracking()
            .Where(e => e.PaymentId == booked.Data.Payment.Id)
            .ToListAsync();
        Assert.Contains(events, e => e.Kind == "verified");
    }

    /// <summary>
    /// What the payments module reports as received today: the sum of the settled payments. Read as
    /// rows and summed here because the SQLite test provider cannot aggregate decimals in SQL — the
    /// rule under test is which payments are in the set, not the arithmetic.
    /// </summary>
    private static async Task<decimal> CollectedTodayAsync(Env env)
    {
        var amounts = await env.Context.Payments.AsNoTracking()
            .Where(p => p.BusinessId == env.Business.Id
                && (p.Status == PaymentStatus.Success || p.Status == PaymentStatus.Verified)
                && p.CompletedAt >= DateTime.UtcNow.Date)
            .Select(p => p.Amount)
            .ToListAsync();

        return amounts.Sum();
    }

    [Fact]
    public async Task NoAccount_RejectionDoesNotTouchTheAppointment()
    {
        using var env = await CreateEnvAsync();

        var booked = await BookAsync(
            env, await RequestWithHoldAsync(env, "0712345678", "wanjiru@example.test", mpesaMessage: ValidMessage));
        Assert.True(booked.Success, booked.Error?.Message);

        var rejected = await env.Payments.RejectPaymentAsync(
            booked.Data!.Payment!.Id,
            new RejectPaymentRequest { Reason = "The code does not appear on the statement." },
            env.Owner.Id, "Business");
        Assert.True(rejected.Success, rejected.Error?.Message);
        Assert.Equal("rejected", rejected.Data!.Status);

        // Rejecting the money does not cancel the visit: only an explicit business rule may do that.
        env.Context.ChangeTracker.Clear();
        var appointment = await env.Context.Appointments.FindAsync(booked.Data.Appointment.Id);
        Assert.Equal(AppointmentStatuses.PendingPayment, appointment!.Status);

        // The customer may try again with a real payment: a rejected payment stays submittable.
        var payment = await env.Context.Payments.AsNoTracking().SingleAsync(p => p.Id == rejected.Data.Id);
        Assert.Equal(PaymentStatus.Rejected, payment.Status);
        Assert.True(PaymentStatuses.CanAcceptSubmission(payment.Status));
    }

    [Fact]
    public async Task NoAccount_WithoutAPastedMessage_IsRefusedAndTheHoldIsTheOnlyClaim()
    {
        using var env = await CreateEnvAsync();

        // The slot is held; the fee is not yet proven. The server must refuse to turn that into a
        // booking: money the studio has not been shown is not a paid-for slot.
        var request = await RequestWithHoldAsync(env, "0712345678", "wanjiru@example.test");
        request.MpesaMessage = null;

        var refused = await BookAsync(env, request);

        Assert.False(refused.Success);
        Assert.Equal("PAYMENT_PROOF_REQUIRED", refused.Error?.Code);
        Assert.Empty(await env.Context.Appointments.ToListAsync());

        // The claim survives, still blocking the interval: the customer can still finish by pasting.
        env.Context.ChangeTracker.Clear();
        var hold = await env.Context.AppointmentSlotHolds.AsNoTracking()
            .SingleAsync(h => h.Id == request.HoldId);
        Assert.Equal(AppointmentSlotHoldStatuses.Active, hold.Status);
    }

    [Fact]
    public async Task NoAccount_WithoutAHold_IsRefused()
    {
        using var env = await CreateEnvAsync();

        // Even with the confirmation in hand, a booking that never claimed the slot is refused: the
        // hold is what stops two customers paying for the same time.
        var request = Request(env, "0712345678", "wanjiru@example.test", mpesaMessage: ValidMessage);

        var refused = await BookAsync(env, request);

        Assert.False(refused.Success);
        Assert.Equal("RESERVATION_REQUIRED", refused.Error?.Code);
        Assert.Empty(await env.Context.Appointments.ToListAsync());
    }

    [Fact]
    public async Task NoAccount_WithAnUnusablePaste_KeepsTheBookingAndFlagsTheFee()
    {
        using var env = await CreateEnvAsync();

        var booked = await BookAsync(env, await RequestWithHoldAsync(
            env, "0712345678", "wanjiru@example.test", mpesaMessage: "i sent the money already"));

        Assert.True(booked.Success, booked.Error?.Message);
        // The booking stands, the customer is told what to do, and nothing was silently dropped: a
        // paste that cannot be parsed is a warning, not a refusal, because the customer did submit
        // something and the studio can still check their statement.
        Assert.Equal(AppointmentStatuses.PendingPayment, booked.Data!.Appointment.Status);
        Assert.NotEmpty(booked.Data.Warnings);
        Assert.Equal(PublicPaymentStates.Unpaid, booked.Data.PaymentState);
        Assert.Equal("created", booked.Data.Payment!.Status);
    }

    // ═══════════════════════════════════════════════════════════
    //  SCENARIO 2 — signed in, but a customer of another business
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task CustomerOfAnotherBusiness_BooksHereWithoutGainingAnythingElse()
    {
        using var env = await CreateEnvAsync();

        // A second business, with a real customer of it.
        var otherOwner = BookingTestBase.CreateOwner($"other-owner-{Guid.NewGuid():N}@test.com");
        var otherBusiness = BookingTestBase.CreateBusiness(otherOwner.Id, "Other Studio");
        var otherCustomer = BookingTestBase.CreateCustomer($"shared-customer-{Guid.NewGuid():N}@test.com");
        otherCustomer.PhoneNumber = "0722000111";
        await BookingTestBase.SeedAsync(env.Context, otherOwner, otherBusiness, otherCustomer,
            new CustomerBusinessEnrollment
            {
                Id = Guid.NewGuid(),
                CustomerId = otherCustomer.Id,
                BusinessId = otherBusiness.Id,
                Status = CustomerBusinessEnrollmentStatus.Active,
                Type = CustomerType.Registered,
                Source = "test",
                EnrolledAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });

        // They book at the FIRST business, signed in.
        var booked = await BookAsync(
            env,
            await RequestWithHoldAsync(env, "0722000111", otherCustomer.Email, name: "Shared Customer",
                mpesaMessage: ValidMessage),
            callerUserId: otherCustomer.Id, callerRole: "Customer");

        Assert.True(booked.Success, booked.Error?.Message);

        // Their identity is reused — no duplicate person — and the appointment is at this business,
        // for the customer they are.
        Assert.Equal(otherCustomer.Id, booked.Data!.Appointment.CustomerId);
        Assert.Equal(env.Business.Id, booked.Data.Appointment.BusinessId);
        Assert.Equal(AppointmentStatuses.PendingPayment, booked.Data.Appointment.Status);

        // The relationship with this business now exists; the other business's record is untouched.
        env.Context.ChangeTracker.Clear();
        var enrollments = await env.Context.CustomerBusinessEnrollments.AsNoTracking()
            .Where(e => e.CustomerId == otherCustomer.Id)
            .Select(e => e.BusinessId)
            .ToListAsync();
        Assert.Equal(2, enrollments.Count);
        Assert.Contains(env.Business.Id, enrollments);
        Assert.Contains(otherBusiness.Id, enrollments);

        // Nothing from the other business leaked into this booking: its own appointments are
        // untouched and it still exists exactly as it was.
        Assert.DoesNotContain(
            await env.Context.Appointments.AsNoTracking()
                .Where(a => a.BusinessId == otherBusiness.Id).ToListAsync(),
            a => a.CustomerId == otherCustomer.Id);
        Assert.Equal("Other Studio", await env.Context.Businesses.AsNoTracking()
            .Where(b => b.Id == otherBusiness.Id).Select(b => b.Name).SingleAsync());
    }

    [Fact]
    public async Task AnOwnerBrowsingTheirOwnStorefront_IsBookedAsACustomer_NotAsTheBusiness()
    {
        using var env = await CreateEnvAsync();

        // The exact case that used to produce "Your account can't book as a customer here": the
        // owner is signed in with a Business token while booking at their own storefront.
        var booked = await BookAsync(
            env, await RequestWithHoldAsync(env, "0733000222", "walk-in@example.test", name: "Walk In",
                mpesaMessage: ValidMessage),
            callerUserId: env.Owner.Id, callerRole: "Business");

        Assert.True(booked.Success, booked.Error?.Message);

        // The booking was made for a CUSTOMER: the owner's admin account is not the customer.
        Assert.NotEqual(env.Owner.Id, booked.Data!.Appointment.CustomerId);
        Assert.Equal(AppointmentStatuses.PendingPayment, booked.Data.Appointment.Status);

        // The booked-through booking awaited the fee's verification, so the lifecycle has an owner
        // action to take — which is the whole point of the payment-first flow.

        env.Context.ChangeTracker.Clear();
        var customer = await env.Context.Users.FindAsync(booked.Data.Appointment.CustomerId);
        Assert.Equal("Walk In", customer!.FullName);
        Assert.Equal(UserRole.Customer, customer.Role);

        var enrollment = await env.Context.CustomerBusinessEnrollments
            .SingleAsync(e => e.CustomerId == customer.Id);
        Assert.Equal(CustomerType.Guest, enrollment.Type);
    }

    // ═══════════════════════════════════════════════════════════
    //  CUSTOMER RECORDS: reused, never duplicated, never shared
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task AReturningCustomerIsReused_NotDuplicated()
    {
        using var env = await CreateEnvAsync();

        var first = await BookAsync(env, await RequestWithHoldAsync(
            env, "0744000333", "returning@example.test", name: "Returning Customer"));
        Assert.True(first.Success, first.Error?.Message);

        // The same person books again — the same number typed differently, the same address — for a
        // different time so the slot guard does not apply.
        var second = await BookAsync(env, await RequestWithHoldAsync(
            env, "+254744000333", "returning@example.test", name: "Returning Customer",
            scheduledAt: Slot.AddHours(1)));
        Assert.True(second.Success, second.Error?.Message);

        Assert.Equal(first.Data!.Appointment.CustomerId, second.Data!.Appointment.CustomerId);

        env.Context.ChangeTracker.Clear();
        Assert.Equal(1, await env.Context.Users.CountAsync(u => u.PhoneNumber == "0744000333"
            || u.PhoneNumber == "+254744000333"));
        Assert.Equal(1, await env.Context.CustomerBusinessEnrollments
            .CountAsync(e => e.CustomerId == first.Data.Appointment.CustomerId));
    }

    [Fact]
    public async Task TheSamePersonAtAnotherBusiness_IsTheirOwnRecordThere()
    {
        using var env = await CreateEnvAsync();

        var first = await BookAsync(env, await RequestWithHoldAsync(
            env, "0755000444", "two-businesses@example.test", name: "Two Businesses"));
        Assert.True(first.Success, first.Error?.Message);

        // A second business, with its own service and staff, booked by the same number and address.
        var otherOwner = BookingTestBase.CreateOwner($"second-owner-{Guid.NewGuid():N}@test.com");
        var otherBusiness = BookingTestBase.CreateBusiness(otherOwner.Id, "Second Studio");
        var otherStaff = BookingTestBase.CreateStaff(otherBusiness.Id, $"second-staff-{Guid.NewGuid():N}@test.com");
        var otherService = BookingTestBase.CreateService(otherBusiness.Id, "Cut", 30, 500m);
        await BookingTestBase.SeedAsync(env.Context, otherOwner, otherBusiness, otherStaff, otherService,
            new BusinessPaymentConfig
            {
                Id = Guid.NewGuid(), BusinessId = otherBusiness.Id, CreatedAt = DateTime.UtcNow,
                CashEnabled = true, ManualMpesaEnabled = false, BookingFeePercentage = 0m,
                PayUpfrontEnabled = false, PayAfterServiceEnabled = true,
            },
            BookingTestBase.CreateAssignment(otherBusiness.Id, otherStaff.Id, otherService.Id),
            BookingTestBase.CreateShift(otherBusiness.Id, otherStaff.Id, SlotDay, 8, 20));

        var second = await BookAsync(env, new PublicBookingRequest
        {
            BusinessId = otherBusiness.Id,
            ServiceIds = [otherService.Id],
            StaffUserId = otherStaff.Id,
            ScheduledAt = Slot,
            PaymentOption = StorefrontPaymentOptions.PayAfterService,
            FullName = "Two Businesses",
            PhoneNumber = "0755000444",
            Email = "two-businesses@example.test",
        });

        Assert.True(second.Success, second.Error?.Message);

        // One account, two relationships: the same person, known to each business separately. The
        // second booking neither touches nor re-tags the first business's relationship.
        env.Context.ChangeTracker.Clear();
        Assert.Equal(first.Data!.Appointment.CustomerId, second.Data!.Appointment.CustomerId);

        var enrollment = await env.Context.CustomerBusinessEnrollments.AsNoTracking()
            .Where(e => e.CustomerId == second.Data.Appointment.CustomerId)
            .Select(e => new { e.BusinessId, e.Type })
            .ToListAsync();
        Assert.Equal(2, enrollment.Count);
        Assert.Contains(enrollment, e => e.BusinessId == env.Business.Id);
        Assert.Contains(enrollment, e => e.BusinessId == otherBusiness.Id);

        // The first business's booking is untouched by the second business's.
        var firstAppointment = await env.Context.Appointments.AsNoTracking()
            .SingleAsync(a => a.Id == first.Data.Appointment.Id);
        Assert.Equal(env.Business.Id, firstAppointment.BusinessId);
        Assert.Equal(AppointmentStatuses.PendingPayment, firstAppointment.Status);

        // Pay-after-service here: the appointment is booked and the whole value is outstanding.
        Assert.Equal(AppointmentStatuses.Booked, second.Data.Appointment.Status);
        Assert.Equal(PublicPaymentStates.Unpaid, second.Data.PaymentState);
        Assert.Equal(500m, second.Data.Summary.Balance);
    }

    [Fact]
    public async Task AnExistingRegisteredCustomerWhoBooksWithoutSigningIn_StaysRegistered()
    {
        using var env = await CreateEnvAsync();

        // A customer of this business who has a real, verified account.
        var existing = BookingTestBase.CreateCustomer($"registered-{Guid.NewGuid():N}@test.com");
        existing.PhoneNumber = "0766000555";
        existing.Auth!.IsVerified = true;
        await BookingTestBase.SeedAsync(env.Context, existing,
            new CustomerBusinessEnrollment
            {
                Id = Guid.NewGuid(),
                CustomerId = existing.Id,
                BusinessId = env.Business.Id,
                Status = CustomerBusinessEnrollmentStatus.Active,
                Type = CustomerType.Registered,
                Source = "test",
                EnrolledAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });

        // They book from the storefront without signing in.
        var booked = await BookAsync(env, await RequestWithHoldAsync(
            env, "0766000555", existing.Email, name: "Already Registered"));

        Assert.True(booked.Success, booked.Error?.Message);

        // The relationship is reused, and Guest tagging never demotes a Registered one.
        Assert.Equal(existing.Id, booked.Data!.Appointment.CustomerId);

        env.Context.ChangeTracker.Clear();
        var enrollment = await env.Context.CustomerBusinessEnrollments.AsNoTracking()
            .SingleAsync(e => e.CustomerId == existing.Id);
        Assert.Equal(CustomerType.Registered, enrollment.Type);
    }

    // ═══════════════════════════════════════════════════════════
    //  INTEGRITY: retries, isolation, and who may verify
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task ARetriedBookingDoesNotCreateASecondAppointmentOrASecondPayment()
    {
        using var env = await CreateEnvAsync();

        var first = await BookAsync(env, await RequestWithHoldAsync(env, "0777000666", "retry@example.test",
            mpesaMessage: ValidMessage, idempotencyKey: "storefront-retry-1"));
        Assert.True(first.Success, first.Error?.Message);

        // The customer taps again with the same details and key — a double-click, a slow
        // network retry, or a refresh after a timeout. The booking intent is the SAME
        // intent, so the server returns the booking it already made instead of creating a
        // second one or opening a second payment. That replay is the idempotency promise;
        // refusing the retry would leave the customer unsure whether their first tap worked.
        var again = await BookAsync(env, await RequestWithHoldAsync(env, "0777000666", "retry@example.test",
            mpesaMessage: ValidMessage, idempotencyKey: "storefront-retry-1"));

        Assert.True(again.Success, again.Error?.Message);
        Assert.Equal(first.Data!.Appointment.Id, again.Data!.Appointment.Id);

        env.Context.ChangeTracker.Clear();
        Assert.Single(await env.Context.Appointments.AsNoTracking()
            .Where(a => a.BusinessId == env.Business.Id).ToListAsync());
        Assert.Single(await env.Context.Payments.AsNoTracking()
            .Where(p => p.BusinessId == env.Business.Id).ToListAsync());
        Assert.Equal(1, await env.Context.Users.CountAsync(u => u.PhoneNumber == "0777000666"));
    }

    [Fact]
    public async Task ABodyNamingAnotherBusiness_IsRefusedOnATenantHost()
    {
        using var env = await CreateEnvAsync();

        // The request arrives on business A's storefront but names business B.
        var otherOwner = BookingTestBase.CreateOwner($"tenant-owner-{Guid.NewGuid():N}@test.com");
        var otherBusiness = BookingTestBase.CreateBusiness(otherOwner.Id, "Tenant Other");

        var refused = await BookAsync(env, await RequestWithHoldAsync(env, "0788000777", "tenant@example.test"),
            tenant: otherBusiness.Id);

        Assert.False(refused.Success);
        Assert.Equal("FORBIDDEN", refused.Error?.Code);
        Assert.Empty(await env.Context.Appointments.ToListAsync());
    }

    [Fact]
    public async Task ALatePaste_AttachesToTheBookingWithoutTouchingTheAppointment()
    {
        using var env = await CreateEnvAsync();

        // Booked with the confirmation the customer pasted at checkout.
        var booked = await BookAsync(env, await RequestWithHoldAsync(env, "0799000888", "late@example.test"));
        Assert.True(booked.Success, booked.Error?.Message);
        Assert.Equal(PublicPaymentStates.PendingVerification, booked.Data!.PaymentState);

        // A second paste on the same booking is not a second claim: the payment is already waiting
        // for the business, so the message is reported back as a warning and the booking is rebuilt
        // from the row that exists.
        var again = await env.Public.SubmitPaymentProofAsync(
            env.Business.Id, booked.Data.Appointment.Id,
            new SubmitPublicPaymentProofRequest { Message = ValidMessage });

        Assert.True(again.Success, again.Error?.Message);
        Assert.NotEmpty(again.Data!.Warnings);
        Assert.Equal(booked.Data.Appointment.Id, again.Data.Appointment.Id);
        Assert.Equal(PublicPaymentStates.PendingVerification, again.Data.PaymentState);

        // Attaching a confirmation never rebuilds the booking: same appointment, same customer,
        // same status, and still exactly one row with exactly one payment.
        env.Context.ChangeTracker.Clear();
        var after = await env.Context.Appointments.AsNoTracking()
            .SingleAsync(a => a.Id == booked.Data.Appointment.Id);
        Assert.Equal(AppointmentStatuses.PendingPayment, after.Status);
        Assert.Equal(booked.Data.Appointment.CustomerId, after.CustomerId);
        Assert.Single(await env.Context.Appointments.ToListAsync());
        Assert.Single(await env.Context.Payments.ToListAsync());
    }

    [Fact]
    public async Task AProofForAnotherBusinessesAppointment_IsNotFound()
    {
        using var env = await CreateEnvAsync();

        var booked = await BookAsync(env, await RequestWithHoldAsync(env, "0711111222", "cross@example.test"));
        Assert.True(booked.Success, booked.Error?.Message);

        var otherOwner = BookingTestBase.CreateOwner($"cross-owner-{Guid.NewGuid():N}@test.com");
        var otherBusiness = BookingTestBase.CreateBusiness(otherOwner.Id, "Cross Studio");
        await BookingTestBase.SeedAsync(env.Context, otherOwner, otherBusiness);

        var refused = await env.Public.SubmitPaymentProofAsync(
            otherBusiness.Id, booked.Data!.Appointment.Id,
            new SubmitPublicPaymentProofRequest { Message = ValidMessage });

        Assert.False(refused.Success);
        Assert.Equal("NOT_FOUND", refused.Error?.Code);
    }

    [Fact]
    public void VerificationAndRejectionAreOwnerOrStaffEndpoints()
    {
        // The administrative decision stays behind the business's own authorization: a customer —
        // signed in or not — cannot approve their own payment. Checked against the controller's own
        // attributes, so a future edit that loosens the policy fails here.
        var controller = typeof(API.Controllers.PaymentsController);

        foreach (var action in new[] { "Approve", "Reject", "ConfirmCash", "MarkUnderReview" })
        {
            var method = controller.GetMethod(action);
            Assert.NotNull(method);
            var policies = method!
                .GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true)
                .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>()
                .Select(a => a.Policy)
                .ToList();
            Assert.Contains(policies, p => p is MembershipPolicies.OwnerOrStaff);
        }
    }
}
