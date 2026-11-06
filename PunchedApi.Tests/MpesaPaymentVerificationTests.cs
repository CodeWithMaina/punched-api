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
/// Manual M-PESA: parser, submission, owner verification, duplicate/amount defences,
/// tenant isolation and the expiry sweep.
///
/// <para>The thread running through every test here is that a pasted SMS is
/// <i>evidence</i>, never proof: nothing a customer submits may settle a payment
/// without an explicit owner decision.</para>
/// </summary>
public class MpesaPaymentVerificationTests
{
    private const string ValidMessage =
        "QJK42NUM01 Confirmed. Ksh1,000.00 sent to NURU STUDIO for account 12345 on 12/5/26 at 3:04 PM. " +
        "New M-PESA balance is Ksh500.00. Transaction cost, Ksh0.00.";

    private static PaymentService CreateService(ApplicationDbContext context)
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

    /// <summary>
    /// Business + owner + customer + a 1,000 KES appointment, plus a manual M-PESA
    /// configuration at the given booking-fee percentage.
    /// </summary>
    private static async Task<(Business business, User owner, User customer, Appointment appointment)>
        SeedAsync(ApplicationDbContext context, decimal bookingFeePercentage = 50m, bool manualEnabled = true)
    {
        var owner = BookingTestBase.CreateOwner();
        var business = BookingTestBase.CreateBusiness(owner.Id);
        var customer = BookingTestBase.CreateCustomer();
        var appointment = new Appointment
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            CustomerId = customer.Id,
            StaffUserId = owner.Id,
            ScheduledAt = DateTime.UtcNow.AddHours(2),
            EndAt = DateTime.UtcNow.AddHours(3),
            Status = AppointmentStatuses.PendingPayment,
            CreatedAt = DateTime.UtcNow
        };
        appointment.Resources.Add(new AppointmentResource
        {
            Id = Guid.NewGuid(),
            AppointmentId = appointment.Id,
            ServiceCatalogItemId = Guid.NewGuid(),
            Name = "Haircut",
            DurationMinutes = 30,
            Price = 1000m,
            CreatedAt = DateTime.UtcNow
        });

        var config = new BusinessPaymentConfig
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            CashEnabled = true,
            MpesaEnabled = false,
            ManualMpesaEnabled = manualEnabled,
            MpesaPaybillNumber = "247247",
            MpesaPaybillAccount = "12345",
            MpesaPayeeName = "NURU STUDIO",
            BookingFeePercentage = bookingFeePercentage,
            CreatedAt = DateTime.UtcNow
        };

        var enrollment = new CustomerBusinessEnrollment
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            BusinessId = business.Id,
            Status = CustomerBusinessEnrollmentStatus.Active,
            Source = "discovery",
            EnrolledAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await BookingTestBase.SeedAsync(context, owner, business, customer, appointment, config, enrollment);
        return (business, owner, customer, appointment);
    }

    /// <summary>Create the booking-fee payment the storefront would create.</summary>
    private static async Task<Payment> CreateBookingFeeAsync(
        ApplicationDbContext context, PaymentService service, Guid appointmentId, Guid customerId)
    {
        var created = await service.CreatePaymentAsync(
            new CreatePaymentRequest { AppointmentId = appointmentId, Method = "mpesa", Type = "booking_fee" },
            customerId, "Customer");
        Assert.True(created.Success, created.Error?.Message);

        context.ChangeTracker.Clear();
        return await context.Payments.Include(p => p.Attempts).Include(p => p.VerificationEvents)
            .SingleAsync(p => p.Id == created.Data!.Id);
    }

    private static string MessageWith(string code, decimal amount) =>
        $"{code} Confirmed. Ksh{amount:N2} sent to NURU STUDIO for account 12345 on 12/5/26 at 3:04 PM. " +
        "New M-PESA balance is Ksh500.00.";

    // ═══════════════════════════════════════════════════════════
    //  PARSER
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void P1_RealConfirmation_YieldsCodeAmountDateAndRecipient()
    {
        var parsed = MpesaMessageParser.Parse(ValidMessage);

        Assert.True(parsed.IsValid, parsed.FailureMessage);
        Assert.Equal("QJK42NUM01", parsed.TransactionCode);
        Assert.Equal(1000.00m, parsed.Amount);
        Assert.NotNull(parsed.TransactionAtUtc);
        // `12/5/26 at 3:04 PM` is East Africa Time (UTC+3) → 12:04 UTC.
        Assert.Equal(12, parsed.TransactionAtUtc!.Value.Hour);
        Assert.Equal(4, parsed.TransactionAtUtc.Value.Minute);
        Assert.Equal("NURU STUDIO", parsed.Recipient);
    }

    [Fact]
    public void P2_LowercaseCodeAndThousandSeparators_AreStillParsed()
    {
        var parsed = MpesaMessageParser.Parse(
            "qjk42num01 Confirmed. Ksh 1,000.00 paid to NURU STUDIO. on 12/5/26 at 3:04 PM. M-PESA balance Ksh0.00.");

        Assert.True(parsed.IsValid, parsed.FailureMessage);
        Assert.Equal("QJK42NUM01", parsed.TransactionCode);
        Assert.Equal(1000.00m, parsed.Amount);
    }

    [Fact]
    public void P3_WrappedMessage_IsParsedLikeASingleLine()
    {
        var parsed = MpesaMessageParser.Parse(
            "QJK42NUM01 Confirmed.\r\nKsh1,000.00 sent to NURU STUDIO for account 12345\r\n" +
            "on 12/5/26 at 3:04 PM. New M-PESA balance is Ksh500.00.");

        Assert.True(parsed.IsValid, parsed.FailureMessage);
        Assert.Equal("QJK42NUM01", parsed.TransactionCode);
        Assert.Equal(1000.00m, parsed.Amount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void P4_EmptyMessage_IsRefused(string? message)
    {
        var parsed = MpesaMessageParser.Parse(message);
        Assert.False(parsed.IsValid);
        Assert.Equal("MPESA_MESSAGE_EMPTY", parsed.FailureCode);
    }

    [Theory]
    [InlineData("Hello, are you open tomorrow?")]
    [InlineData("I will pay you later today, promise.")]
    [InlineData("M-PESA is down at the moment, I'll try again.")]
    [InlineData("Please confirm my booking, I sent the money.")]
    public void P5_UnrelatedText_IsRefused(string message)
    {
        var parsed = MpesaMessageParser.Parse(message);
        Assert.False(parsed.IsValid);
        Assert.Equal("MPESA_MESSAGE_NOT_CONFIRMATION", parsed.FailureCode);
    }

    [Fact]
    public void P6_ConfirmationWithNoReceiptCode_IsRefused()
    {
        var parsed = MpesaMessageParser.Parse(
            "Confirmed. Ksh1,000.00 sent to NURU STUDIO for account 12345 on 12/5/26 at 3:04 PM. M-PESA balance Ksh0.00.");

        Assert.False(parsed.IsValid);
        Assert.Equal("MPESA_MESSAGE_NO_CODE", parsed.FailureCode);
    }

    [Fact]
    public void P7_ConfirmationWithNoAmount_IsRefused()
    {
        var parsed = MpesaMessageParser.Parse(
            "QJK42NUM01 Confirmed. Sent to NURU STUDIO for account 12345 on 12/5/26 at 3:04 PM. M-PESA balance unchanged.");

        Assert.False(parsed.IsValid);
        Assert.Equal("MPESA_MESSAGE_NO_AMOUNT", parsed.FailureCode);
    }

    [Fact]
    public void P8_AnAllDigitTokenIsNeverMistakenForAReceiptCode()
    {
        // A phone number is not a receipt code, even though it is long and numeric.
        var parsed = MpesaMessageParser.Parse(
            "254712345678 Confirmed. Ksh1,000.00 sent to NURU STUDIO. M-PESA balance Ksh0.00.");

        Assert.False(parsed.IsValid);
        Assert.Equal("MPESA_MESSAGE_NO_CODE", parsed.FailureCode);
    }

    [Fact]
    public void P9_OversizedMessage_IsRefusedBeforeAnyFieldExtraction()
    {
        var parsed = MpesaMessageParser.Parse(new string('x', MpesaMessageParser.MaxMessageLength + 1));
        Assert.False(parsed.IsValid);
        Assert.Equal("MPESA_MESSAGE_TOO_LONG", parsed.FailureCode);
    }

    // ═══════════════════════════════════════════════════════════
    //  CREATION (booking fee from the business's own percentage)
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task C1_ManualMpesaPayment_IsCreatedPendingWithNoProviderCall()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var (_, _, customer, appointment) = await SeedAsync(context);
        var service = CreateService(context);

        var payment = await CreateBookingFeeAsync(context, service, appointment.Id, customer.Id);

        // 50% of 1,000 — the configured percentage, not a platform constant.
        Assert.Equal(500m, payment.Amount);
        Assert.Equal(PaymentType.BookingFee, payment.Type);
        Assert.Equal(PaymentProviderKind.ManualMpesa, payment.Provider);
        Assert.Equal(PaymentStatus.Created, payment.Status);
        // No STK attempt exists: nothing was ever pushed to a handset.
        Assert.Empty(payment.Attempts);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(10, 100)]
    [InlineData(33.5, 335)]
    [InlineData(100, 1000)]
    public async Task C2_BookingFee_IsTheConfiguredPercentageOfTheServiceTotal(decimal percentage, decimal expected)
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var (_, _, customer, appointment) = await SeedAsync(context, percentage);
        var service = CreateService(context);

        var created = await service.CreatePaymentAsync(
            new CreatePaymentRequest { AppointmentId = appointment.Id, Method = "mpesa", Type = "booking_fee" },
            customer.Id, "Customer");

        if (expected == 0)
        {
            // A percentage of 0 means "nothing up front", reported as such rather than
            // silently charging a default.
            Assert.False(created.Success);
            Assert.Equal("VALIDATION_ERROR", created.Error?.Code);
            return;
        }

        Assert.True(created.Success, created.Error?.Message);
        Assert.Equal(expected, created.Data!.Amount);
    }

    [Fact]
    public async Task C3_ManualChannelOff_StillRequiresDarajaConfiguration()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var (_, _, customer, appointment) = await SeedAsync(context, manualEnabled: false);
        var service = CreateService(context);

        var created = await service.CreatePaymentAsync(
            new CreatePaymentRequest { AppointmentId = appointment.Id, Method = "mpesa", Type = "booking_fee" },
            customer.Id, "Customer");

        Assert.False(created.Success);
        Assert.Equal("VALIDATION_ERROR", created.Error?.Code);
    }

    // ═══════════════════════════════════════════════════════════
    //  SUBMISSION
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task S1_ValidSubmission_QueuesForReviewAndNeverSettles()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var (_, _, customer, appointment) = await SeedAsync(context);
        var service = CreateService(context);
        var payment = await CreateBookingFeeAsync(context, service, appointment.Id, customer.Id);

        var submitted = await service.SubmitMpesaMessageAsync(
            payment.Id, new SubmitMpesaMessageRequest { Message = MessageWith("QJK42NUM01", 500m) },
            customer.Id, "Customer");

        Assert.True(submitted.Success, submitted.Error?.Message);
        Assert.Equal("submitted", submitted.Data!.Status);
        Assert.Equal("QJK42NUM01", submitted.Data.ParsedTransactionCode);
        Assert.Equal(500m, submitted.Data.ParsedAmount);
        Assert.Equal(MessageWith("QJK42NUM01", 500m), submitted.Data.SubmittedMessage);

        // The money is NOT considered received, and the appointment is untouched.
        context.ChangeTracker.Clear();
        var stored = await context.Payments.SingleAsync(p => p.Id == payment.Id);
        Assert.Equal(PaymentStatus.Submitted, stored.Status);
        Assert.False(PaymentStatuses.IsSettled(stored.Status));
        Assert.Null(stored.CompletedAt);

        var refreshedAppointment = await context.Appointments.SingleAsync(a => a.Id == appointment.Id);
        Assert.Equal(AppointmentStatuses.PendingPayment, refreshedAppointment.Status);

        // An append-only trail entry was written for the submission.
        var events = await context.PaymentVerificationEvents
            .Where(e => e.PaymentId == payment.Id).ToListAsync();
        Assert.Single(events);
        Assert.Equal("submitted", events[0].Kind);
        Assert.Equal("QJK42NUM01", events[0].TransactionCode);
    }

    [Fact]
    public async Task S2_AmountMismatch_IsRefusedImmediately()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var (_, _, customer, appointment) = await SeedAsync(context);
        var service = CreateService(context);
        var payment = await CreateBookingFeeAsync(context, service, appointment.Id, customer.Id);

        var submitted = await service.SubmitMpesaMessageAsync(
            payment.Id, new SubmitMpesaMessageRequest { Message = MessageWith("QJK42NUM02", 100m) },
            customer.Id, "Customer");

        Assert.False(submitted.Success);
        Assert.Equal("MPESA_AMOUNT_MISMATCH", submitted.Error?.Code);

        context.ChangeTracker.Clear();
        Assert.Equal(PaymentStatus.Created, (await context.Payments.SingleAsync()).Status);
        Assert.Empty(await context.PaymentVerificationEvents.ToListAsync());
    }

    [Fact]
    public async Task S3_UnrelatedText_IsRefusedAndNothingIsWritten()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var (_, _, customer, appointment) = await SeedAsync(context);
        var service = CreateService(context);
        var payment = await CreateBookingFeeAsync(context, service, appointment.Id, customer.Id);

        var submitted = await service.SubmitMpesaMessageAsync(
            payment.Id, new SubmitMpesaMessageRequest { Message = "I paid, trust me!" },
            customer.Id, "Customer");

        Assert.False(submitted.Success);
        Assert.Equal("MPESA_MESSAGE_NOT_CONFIRMATION", submitted.Error?.Code);
        Assert.Equal(PaymentStatus.Created, (await context.Payments.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task S4_ReusedTransactionCode_IsRefused()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var (business, _, customer, appointment) = await SeedAsync(context);
        var service = CreateService(context);
        var first = await CreateBookingFeeAsync(context, service, appointment.Id, customer.Id);

        var second = new Payment
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            CustomerId = customer.Id,
            AppointmentId = appointment.Id,
            Type = PaymentType.BookingFee,
            Method = PaymentMethod.Mpesa,
            Provider = PaymentProviderKind.ManualMpesa,
            Status = PaymentStatus.Created,
            Amount = 500m,
            Currency = "KES",
            Reference = "PMT-SECOND0001",
            CreatedAt = DateTime.UtcNow
        };
        context.Payments.Add(second);
        await context.SaveChangesAsync();

        var firstSubmit = await service.SubmitMpesaMessageAsync(
            first.Id, new SubmitMpesaMessageRequest { Message = MessageWith("QJK42NUM03", 500m) },
            customer.Id, "Customer");
        Assert.True(firstSubmit.Success, firstSubmit.Error?.Message);

        var secondSubmit = await service.SubmitMpesaMessageAsync(
            second.Id, new SubmitMpesaMessageRequest { Message = MessageWith("QJK42NUM03", 500m) },
            customer.Id, "Customer");

        Assert.False(secondSubmit.Success);
        Assert.Equal("MPESA_CODE_ALREADY_USED", secondSubmit.Error?.Code);
    }

    [Fact]
    public async Task S5_ASecondSubmissionWhileOneIsPending_IsRefused()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var (_, _, customer, appointment) = await SeedAsync(context);
        var service = CreateService(context);
        var payment = await CreateBookingFeeAsync(context, service, appointment.Id, customer.Id);

        var first = await service.SubmitMpesaMessageAsync(
            payment.Id, new SubmitMpesaMessageRequest { Message = MessageWith("QJK42NUM04", 500m) },
            customer.Id, "Customer");
        Assert.True(first.Success, first.Error?.Message);

        var second = await service.SubmitMpesaMessageAsync(
            payment.Id, new SubmitMpesaMessageRequest { Message = MessageWith("QJK42NUM05", 500m) },
            customer.Id, "Customer");

        Assert.False(second.Success);
        Assert.Equal("INVALID_STATUS_TRANSITION", second.Error?.Code);
    }

    [Fact]
    public async Task S6_AnotherCustomersPayment_IsNotFound()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var (_, _, customer, appointment) = await SeedAsync(context);
        var service = CreateService(context);
        var payment = await CreateBookingFeeAsync(context, service, appointment.Id, customer.Id);

        var stranger = BookingTestBase.CreateCustomer("stranger@test.com");
        await BookingTestBase.SeedAsync(context, stranger);

        var submitted = await service.SubmitMpesaMessageAsync(
            payment.Id, new SubmitMpesaMessageRequest { Message = MessageWith("QJK42NUM06", 500m) },
            stranger.Id, "Customer");

        Assert.False(submitted.Success);
        Assert.Equal("NOT_FOUND", submitted.Error?.Code);
    }

    // ═══════════════════════════════════════════════════════════
    //  OWNER DECISIONS
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task V1_Approve_SettlesThePaymentAndConfirmsTheAppointment()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var (_, owner, customer, appointment) = await SeedAsync(context);
        var service = CreateService(context);
        var payment = await CreateBookingFeeAsync(context, service, appointment.Id, customer.Id);
        await service.SubmitMpesaMessageAsync(
            payment.Id, new SubmitMpesaMessageRequest { Message = MessageWith("QJK42NUM10", 500m) },
            customer.Id, "Customer");

        var approved = await service.VerifyPaymentAsync(
            payment.Id, new VerifyPaymentRequest { Note = "Saw it on the till statement." }, owner.Id, "Business");

        Assert.True(approved.Success, approved.Error?.Message);
        Assert.Equal("verified", approved.Data!.Status);
        Assert.Equal(owner.Id, approved.Data.VerifiedByUserId);
        Assert.NotNull(approved.Data.CompletedAt);

        context.ChangeTracker.Clear();
        var stored = await context.Payments.SingleAsync(p => p.Id == payment.Id);
        Assert.Equal(PaymentStatus.Verified, stored.Status);
        Assert.True(PaymentStatuses.IsSettled(stored.Status));

        var refreshed = await context.Appointments.SingleAsync(a => a.Id == appointment.Id);
        Assert.Equal(AppointmentStatuses.Confirmed, refreshed.Status);

        // The confirmation is in the appointment's own history, not just the payment's.
        var history = await context.AppointmentStatusHistory
            .Where(h => h.AppointmentId == appointment.Id).ToListAsync();
        Assert.Contains(history, h => h.Status == AppointmentStatuses.Confirmed);

        var kinds = await context.PaymentVerificationEvents
            .Where(e => e.PaymentId == payment.Id).Select(e => e.Kind).ToListAsync();
        Assert.Equal(new[] { "submitted", "verified" }, kinds);
    }

    [Fact]
    public async Task V2_ApprovingTwice_IsIdempotentAndDoesNotRepeatSideEffects()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var (_, owner, customer, appointment) = await SeedAsync(context);
        var service = CreateService(context);
        var payment = await CreateBookingFeeAsync(context, service, appointment.Id, customer.Id);
        await service.SubmitMpesaMessageAsync(
            payment.Id, new SubmitMpesaMessageRequest { Message = MessageWith("QJK42NUM11", 500m) },
            customer.Id, "Customer");

        var first = await service.VerifyPaymentAsync(payment.Id, new VerifyPaymentRequest(), owner.Id, "Business");
        var second = await service.VerifyPaymentAsync(payment.Id, new VerifyPaymentRequest(), owner.Id, "Business");

        Assert.True(first.Success);
        Assert.True(second.Success);

        Assert.Single(await context.PaymentVerificationEvents
            .Where(e => e.PaymentId == payment.Id && e.Kind == "verified").ToListAsync());
        Assert.Single(await context.AppointmentStatusHistory
            .Where(h => h.AppointmentId == appointment.Id && h.Status == AppointmentStatuses.Confirmed)
            .ToListAsync());
    }

    [Fact]
    public async Task V3_Reject_RecordsTheReasonAndLeavesTheSlotHeld()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var (_, owner, customer, appointment) = await SeedAsync(context);
        var service = CreateService(context);
        var payment = await CreateBookingFeeAsync(context, service, appointment.Id, customer.Id);
        await service.SubmitMpesaMessageAsync(
            payment.Id, new SubmitMpesaMessageRequest { Message = MessageWith("QJK42NUM12", 500m) },
            customer.Id, "Customer");

        var rejected = await service.RejectPaymentAsync(
            payment.Id, new RejectPaymentRequest { Reason = "This code is for a different till." },
            owner.Id, "Business");

        Assert.True(rejected.Success, rejected.Error?.Message);
        Assert.Equal("rejected", rejected.Data!.Status);
        Assert.Equal("This code is for a different till.", rejected.Data.RejectionReason);

        context.ChangeTracker.Clear();
        // The appointment keeps holding its slot so the customer can pay again.
        Assert.Equal(AppointmentStatuses.PendingPayment,
            (await context.Appointments.SingleAsync(a => a.Id == appointment.Id)).Status);
        Assert.False(PaymentStatuses.IsSettled((await context.Payments.SingleAsync()).Status));
    }

    [Fact]
    public async Task V4_RejectWithoutAReason_IsRefused()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var (_, owner, customer, appointment) = await SeedAsync(context);
        var service = CreateService(context);
        var payment = await CreateBookingFeeAsync(context, service, appointment.Id, customer.Id);
        await service.SubmitMpesaMessageAsync(
            payment.Id, new SubmitMpesaMessageRequest { Message = MessageWith("QJK42NUM13", 500m) },
            customer.Id, "Customer");

        var rejected = await service.RejectPaymentAsync(
            payment.Id, new RejectPaymentRequest { Reason = "   " }, owner.Id, "Business");

        Assert.False(rejected.Success);
        Assert.Equal("VALIDATION_ERROR", rejected.Error?.Code);
    }

    [Fact]
    public async Task V5_AfterARejection_TheCustomerMaySubmitANewCode()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var (_, owner, customer, appointment) = await SeedAsync(context);
        var service = CreateService(context);
        var payment = await CreateBookingFeeAsync(context, service, appointment.Id, customer.Id);

        await service.SubmitMpesaMessageAsync(
            payment.Id, new SubmitMpesaMessageRequest { Message = MessageWith("QJK42NUM14", 500m) },
            customer.Id, "Customer");
        await service.RejectPaymentAsync(
            payment.Id, new RejectPaymentRequest { Reason = "Wrong amount." }, owner.Id, "Business");

        var resubmitted = await service.SubmitMpesaMessageAsync(
            payment.Id, new SubmitMpesaMessageRequest { Message = MessageWith("QJK42NUM15", 500m) },
            customer.Id, "Customer");

        Assert.True(resubmitted.Success, resubmitted.Error?.Message);
        Assert.Equal("submitted", resubmitted.Data!.Status);
        Assert.Equal("QJK42NUM15", resubmitted.Data.ParsedTransactionCode);
        Assert.Null(resubmitted.Data.RejectionReason);

        var approved = await service.VerifyPaymentAsync(
            payment.Id, new VerifyPaymentRequest(), owner.Id, "Business");
        Assert.True(approved.Success, approved.Error?.Message);
    }

    [Fact]
    public async Task V6_VerifyingARejectedPayment_IsRefused()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var (_, owner, customer, appointment) = await SeedAsync(context);
        var service = CreateService(context);
        var payment = await CreateBookingFeeAsync(context, service, appointment.Id, customer.Id);
        await service.SubmitMpesaMessageAsync(
            payment.Id, new SubmitMpesaMessageRequest { Message = MessageWith("QJK42NUM16", 500m) },
            customer.Id, "Customer");
        await service.RejectPaymentAsync(
            payment.Id, new RejectPaymentRequest { Reason = "Not ours." }, owner.Id, "Business");

        var approved = await service.VerifyPaymentAsync(
            payment.Id, new VerifyPaymentRequest(), owner.Id, "Business");

        Assert.False(approved.Success);
        Assert.Equal("INVALID_STATUS_TRANSITION", approved.Error?.Code);
    }

    [Fact]
    public async Task V7_AnotherBusiness_CannotDecideOnThisPayment()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var (_, _, customer, appointment) = await SeedAsync(context);
        var service = CreateService(context);
        var payment = await CreateBookingFeeAsync(context, service, appointment.Id, customer.Id);
        await service.SubmitMpesaMessageAsync(
            payment.Id, new SubmitMpesaMessageRequest { Message = MessageWith("QJK42NUM17", 500m) },
            customer.Id, "Customer");

        // A second business with its own owner — same database, different tenant.
        var rivalOwner = BookingTestBase.CreateOwner("rival@test.com");
        var rivalBusiness = BookingTestBase.CreateBusiness(rivalOwner.Id, "Rival Studio");
        await BookingTestBase.SeedAsync(context, rivalOwner, rivalBusiness);

        var approved = await service.VerifyPaymentAsync(
            payment.Id, new VerifyPaymentRequest(), rivalOwner.Id, "Business");

        Assert.False(approved.Success);
        Assert.Equal("NOT_FOUND", approved.Error?.Code);

        context.ChangeTracker.Clear();
        Assert.Equal(PaymentStatus.Submitted, (await context.Payments.SingleAsync()).Status);
    }

    // ═══════════════════════════════════════════════════════════
    //  OWNER QUEUE
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Q1_QueueListsWhatIsAwaitingAndCountsTheMoney()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var (_, owner, customer, appointment) = await SeedAsync(context);
        var service = CreateService(context);
        var payment = await CreateBookingFeeAsync(context, service, appointment.Id, customer.Id);
        await service.SubmitMpesaMessageAsync(
            payment.Id, new SubmitMpesaMessageRequest { Message = MessageWith("QJK42NUM20", 500m) },
            customer.Id, "Customer");

        var queue = await service.GetVerificationQueueAsync(
            new PaymentVerificationQuery(), owner.Id, "Business");

        Assert.True(queue.Success, queue.Error?.Message);
        Assert.Equal(1, queue.Data!.AwaitingCount);
        Assert.Equal(500m, queue.Data.AwaitingAmount);
        Assert.Single(queue.Data.Items);

        var item = queue.Data.Items[0];
        Assert.Equal(500m, item.ExpectedAmount);
        Assert.Equal(500m, item.SubmittedAmount);
        Assert.Equal("QJK42NUM20", item.TransactionCode);
        Assert.False(item.AmountMismatch);
        Assert.Null(item.DuplicateOfPaymentId);
        Assert.Equal(customer.FullName, item.CustomerName);
        Assert.Equal(AppointmentStatuses.PendingPayment, item.AppointmentStatus);
        Assert.Equal(new[] { "Haircut" }, item.ServiceNames);
        Assert.Contains("QJK42NUM20", item.SubmittedMessage);
    }

    [Fact]
    public async Task Q2_QueueIsEmptyForAnUnrelatedBusiness()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var (_, _, customer, appointment) = await SeedAsync(context);
        var service = CreateService(context);
        var payment = await CreateBookingFeeAsync(context, service, appointment.Id, customer.Id);
        await service.SubmitMpesaMessageAsync(
            payment.Id, new SubmitMpesaMessageRequest { Message = MessageWith("QJK42NUM21", 500m) },
            customer.Id, "Customer");

        var rivalOwner = BookingTestBase.CreateOwner("rival2@test.com");
        var rivalBusiness = BookingTestBase.CreateBusiness(rivalOwner.Id, "Rival Two");
        await BookingTestBase.SeedAsync(context, rivalOwner, rivalBusiness);

        var queue = await service.GetVerificationQueueAsync(
            new PaymentVerificationQuery(), rivalOwner.Id, "Business");

        Assert.True(queue.Success, queue.Error?.Message);
        Assert.Empty(queue.Data!.Items);
        Assert.Equal(0, queue.Data.AwaitingCount);
    }

    // ═══════════════════════════════════════════════════════════
    //  EXPIRY
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task E1_AStaleSubmission_LapsesAndReleasesTheSlot()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var (_, _, customer, appointment) = await SeedAsync(context);
        var service = CreateService(context);
        var payment = await CreateBookingFeeAsync(context, service, appointment.Id, customer.Id);
        await service.SubmitMpesaMessageAsync(
            payment.Id, new SubmitMpesaMessageRequest { Message = MessageWith("QJK42NUM22", 500m) },
            customer.Id, "Customer");

        // Age the submission past the verification window.
        var stored = await context.Payments.SingleAsync(p => p.Id == payment.Id);
        stored.SubmittedAt = DateTime.UtcNow.AddDays(-4);
        stored.CreatedAt = DateTime.UtcNow.AddDays(-4);
        await context.SaveChangesAsync();

        var expired = await service.ExpireStaleVerificationsAsync();
        Assert.Equal(1, expired);

        context.ChangeTracker.Clear();
        Assert.Equal(PaymentStatus.Expired, (await context.Payments.SingleAsync()).Status);
        // The slot the abandoned booking was holding is released.
        Assert.Equal(AppointmentStatuses.Cancelled,
            (await context.Appointments.SingleAsync(a => a.Id == appointment.Id)).Status);
    }

    [Fact]
    public async Task E2_AFreshSubmission_IsNotTouchedByTheSweep()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var (_, _, customer, appointment) = await SeedAsync(context);
        var service = CreateService(context);
        var payment = await CreateBookingFeeAsync(context, service, appointment.Id, customer.Id);
        await service.SubmitMpesaMessageAsync(
            payment.Id, new SubmitMpesaMessageRequest { Message = MessageWith("QJK42NUM23", 500m) },
            customer.Id, "Customer");

        Assert.Equal(0, await service.ExpireStaleVerificationsAsync());
        Assert.Equal(PaymentStatus.Submitted, (await context.Payments.AsNoTracking().SingleAsync()).Status);
    }
}