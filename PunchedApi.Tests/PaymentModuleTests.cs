using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using PunchedApi.Application.Authorization;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;
using PunchedApi.Infrastructure.Repositories;
using PunchedApi.Infrastructure.Services.Payments;
using PunchedApi.Application.Settings;

namespace PunchedApi.Tests;

/// <summary>
/// Payment module tests: state machine, cash lifecycle, tenant isolation,
/// retry rules, and callback idempotency (duplicate STK callback must never
/// produce a second success).
/// </summary>
public class PaymentModuleTests
{
    private static PaymentService CreatePaymentService(ApplicationDbContext context)
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
            NullLogger<PaymentService>.Instance);
    }

    private static PaymentCallbackService CreateCallbackService(ApplicationDbContext context)
        => new(context, new UnitOfWork(context), NullLogger<PaymentCallbackService>.Instance);

    /// <summary>Seeds business + owner + customer + appointment with one 1,000 KES service.</summary>
    private static async Task<(Business business, User owner, User customer, Appointment appointment)>
        SeedAsync(ApplicationDbContext context)
    {
        var owner = BookingTestBase.CreateOwner();
        var business = BookingTestBase.CreateBusiness(owner.Id);
        var customer = BookingTestBase.CreateCustomer();
        var appointment = new Appointment
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            CustomerId = customer.Id,
            ScheduledAt = DateTime.UtcNow.AddHours(2),
            EndAt = DateTime.UtcNow.AddHours(3),
            Status = "booked",
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
        await BookingTestBase.SeedAsync(context, owner, business, customer, appointment);
        return (business, owner, customer, appointment);
    }

    // ── State machine ────────────────────────────────────────

    [Fact]
    public void SM1_ValidTransitions_AreAllowed()
    {
        Assert.True(PaymentStateMachine.CanTransition(PaymentStatus.Created, PaymentStatus.Success));
        Assert.True(PaymentStateMachine.CanTransition(PaymentStatus.AwaitingCustomer, PaymentStatus.Success));
        Assert.True(PaymentStateMachine.CanTransition(PaymentStatus.AwaitingCustomer, PaymentStatus.Failed));
        Assert.True(PaymentStateMachine.CanTransition(PaymentStatus.Failed, PaymentStatus.AwaitingCustomer));
        Assert.True(PaymentStateMachine.CanTransition(PaymentStatus.Expired, PaymentStatus.AwaitingCustomer));
        Assert.True(PaymentStateMachine.CanTransition(PaymentStatus.Success, PaymentStatus.Reversed));
    }

    [Fact]
    public void SM2_SuccessCannotGoBackToPending()
    {
        Assert.False(PaymentStateMachine.CanTransition(PaymentStatus.Success, PaymentStatus.Created));
        Assert.False(PaymentStateMachine.CanTransition(PaymentStatus.Success, PaymentStatus.AwaitingCustomer));
        Assert.False(PaymentStateMachine.CanTransition(PaymentStatus.Success, PaymentStatus.Failed));
        Assert.False(PaymentStateMachine.CanTransition(PaymentStatus.Reversed, PaymentStatus.Success));
    }

    [Fact]
    public void SM3_InvalidTransition_Throws()
    {
        var payment = new Payment { Status = PaymentStatus.Cancelled };
        Assert.Throws<InvalidPaymentTransitionException>(
            () => PaymentStateMachine.Transition(payment, PaymentStatus.Success));
    }

    // ── Cash lifecycle ───────────────────────────────────────

    [Fact]
    public async Task C1_CashPayment_AmountComputedServerSide_ThenConfirmed()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var (business, owner, customer, appointment) = await SeedAsync(context);
        var service = CreatePaymentService(context);

        // The client never sends an amount — the backend derives it from the appointment.
        var created = await service.CreatePaymentAsync(
            new CreatePaymentRequest { AppointmentId = appointment.Id, Method = "cash" },
            customer.Id, "Customer");
        Assert.True(created.Success, created.Error?.Message);
        Assert.Equal(1000m, created.Data!.Amount);
        Assert.Equal("created", created.Data.Status);
        Assert.Equal("KES", created.Data.Currency);
        Assert.StartsWith("PMT-", created.Data.Reference);

        // Confirmation is done by the business, in the present — never backdated.
        var confirmed = await service.ConfirmCashAsync(created.Data.Id, "Received at counter", owner.Id, "Business");
        Assert.True(confirmed.Success, confirmed.Error?.Message);
        Assert.Equal("success", confirmed.Data!.Status);
        Assert.Equal("CASH-" + created.Data.Reference, confirmed.Data.ExternalReference);

        context.ChangeTracker.Clear();

        // Idempotent: confirming again returns the same success — no second attempt row.
        var again = await service.ConfirmCashAsync(created.Data.Id, "Second scan", owner.Id, "Business");
        Assert.True(again.Success);
        Assert.Equal("success", again.Data!.Status);
        Assert.Equal(1, await context.Payments.CountAsync());
        Assert.Equal(1, await context.PaymentAttempts.CountAsync());
    }

    [Fact]
    public async Task C2_TenantIsolation_OtherBusinessCannotConfirmOrSee()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var (business, owner, customer, appointment) = await SeedAsync(context);
        var service = CreatePaymentService(context);

        var created = await service.CreatePaymentAsync(
            new CreatePaymentRequest { AppointmentId = appointment.Id, Method = "cash" },
            customer.Id, "Customer");

        // A different business's owner cannot even see the payment.
        var otherOwner = BookingTestBase.CreateOwner("other@test.com");
        var otherBusiness = BookingTestBase.CreateBusiness(otherOwner.Id, "Other Business");
        await BookingTestBase.SeedAsync(context, otherOwner, otherBusiness);

        var foreign = await service.GetPaymentAsync(created.Data!.Id, otherOwner.Id, "Business");
        Assert.False(foreign.Success);
        Assert.Equal("NOT_FOUND", foreign.Error!.Code);

        var confirm = await service.ConfirmCashAsync(created.Data.Id, "nope", otherOwner.Id, "Business");
        Assert.False(confirm.Success);
        Assert.Equal("NOT_FOUND", confirm.Error!.Code);
    }

    [Fact]
    public async Task C3_CancelledPayment_CannotBeConfirmed()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var (business, owner, customer, appointment) = await SeedAsync(context);
        var service = CreatePaymentService(context);

        var created = await service.CreatePaymentAsync(
            new CreatePaymentRequest { AppointmentId = appointment.Id, Method = "cash" },
            customer.Id, "Customer");
        await service.CancelPaymentAsync(created.Data!.Id, customer.Id, "Customer");

        var confirm = await service.ConfirmCashAsync(created.Data.Id, "late", owner.Id, "Business");
        Assert.False(confirm.Success);
        Assert.Equal("INVALID_STATUS_TRANSITION", confirm.Error!.Code);
    }

    // ── Retry rules ──────────────────────────────────────────

    [Fact]
    public async Task R1_SuccessPayment_CannotBeRetried()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var (business, owner, customer, appointment) = await SeedAsync(context);
        var service = CreatePaymentService(context);

        var created = await service.CreatePaymentAsync(
            new CreatePaymentRequest { AppointmentId = appointment.Id, Method = "cash" },
            customer.Id, "Customer");
        await service.ConfirmCashAsync(created.Data!.Id, null, owner.Id, "Business");

        var retry = await service.RetryPaymentAsync(created.Data.Id, customer.Id, "Customer");
        Assert.False(retry.Success);
        Assert.Equal("INVALID_STATUS_TRANSITION", retry.Error!.Code);
    }

    // ── Callback idempotency ─────────────────────────────────

    private static string StkSuccessPayload(string checkoutRequestId, string receipt, decimal amount) =>
        JsonSerializer.Serialize(new
        {
            Body = new
            {
                stkCallback = new
                {
                    MerchantRequestID = "mr-1",
                    CheckoutRequestID = checkoutRequestId,
                    ResultCode = 0,
                    ResultDesc = "The service request is processed successfully.",
                    CallbackMetadata = new
                    {
                        Item = new object[]
                        {
                            new { Name = "Amount", Value = amount },
                            new { Name = "MpesaReceiptNumber", Value = receipt },
                            new { Name = "PhoneNumber", Value = 254712345678L }
                        }
                    }
                }
            }
        });

    private static async Task<Payment> SeedStkPaymentAsync(
        ApplicationDbContext context, Business business, Appointment appointment, string checkoutRequestId)
    {
        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            CustomerId = appointment.CustomerId,
            AppointmentId = appointment.Id,
            Type = PaymentType.FullPayment,
            Method = PaymentMethod.Mpesa,
            Provider = PaymentProviderKind.DarajaStk,
            Status = PaymentStatus.AwaitingCustomer,
            Amount = 1000m,
            Currency = "KES",
            Reference = "PMT-TEST-" + checkoutRequestId[^4..],
            PhoneNumber = "254712345678",
            CreatedAt = DateTime.UtcNow
        };
        payment.Attempts.Add(new PaymentAttempt
        {
            Id = Guid.NewGuid(),
            PaymentId = payment.Id,
            AttemptNumber = 1,
            Provider = PaymentProviderKind.DarajaStk,
            Status = "pending",
            CheckoutRequestId = checkoutRequestId,
            MerchantRequestId = "mr-1",
            CreatedAt = DateTime.UtcNow
        });
        await BookingTestBase.SeedAsync(context, payment);
        return payment;
    }

    [Fact]
    public async Task CB1_DuplicateStkCallback_NeverCreatesSecondSuccess()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var (business, _, _, appointment) = await SeedAsync(context);
        var callbackService = CreateCallbackService(context);
        await SeedStkPaymentAsync(context, business, appointment, "ws_CO_TEST_1");

        var payload = StkSuccessPayload("ws_CO_TEST_1", "QJK71XYZ", 1000m);

        var first = await callbackService.ProcessStkCallbackAsync(payload);
        Assert.Equal("applied", first.outcome);

        context.ChangeTracker.Clear();
        var second = await callbackService.ProcessStkCallbackAsync(payload);
        Assert.Equal("duplicate", second.outcome);

        context.ChangeTracker.Clear();
        var stored = await context.Payments.Include(p => p.Attempts).SingleAsync();
        Assert.Equal(PaymentStatus.Success, stored.Status);
        Assert.Equal("QJK71XYZ", stored.ExternalReference);
        Assert.Single(stored.Attempts);
        Assert.Single(await context.PaymentCallbacks.ToListAsync());
    }

    [Fact]
    public async Task CB2_FailureCallback_MarksPaymentFailed_AndRemainsRetryable()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var (business, _, _, appointment) = await SeedAsync(context);
        var callbackService = CreateCallbackService(context);
        await SeedStkPaymentAsync(context, business, appointment, "ws_CO_TEST_2");

        var payload = JsonSerializer.Serialize(new
        {
            Body = new
            {
                stkCallback = new
                {
                    MerchantRequestID = "mr-2",
                    CheckoutRequestID = "ws_CO_TEST_2",
                    ResultCode = 1032,
                    ResultDesc = "Request cancelled by user"
                }
            }
        });

        var result = await callbackService.ProcessStkCallbackAsync(payload);
        Assert.Equal("applied", result.outcome);

        context.ChangeTracker.Clear();
        var stored = await context.Payments.Include(p => p.Attempts).SingleAsync();
        Assert.Equal(PaymentStatus.Failed, stored.Status);
        Assert.NotNull(stored.FailedAt);
        Assert.Single(stored.Attempts);
        Assert.Equal("failed", stored.Attempts.Single().Status);

        // A failed payment is retryable: the state machine allows Failed → AwaitingCustomer.
        Assert.True(PaymentStateMachine.CanTransition(stored.Status, PaymentStatus.AwaitingCustomer));
    }

    [Fact]
    public async Task CB3_UnknownCheckoutId_IsStoredAsUnmatched()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        await SeedAsync(context);
        var callbackService = CreateCallbackService(context);

        var payload = StkSuccessPayload("ws_CO_UNKNOWN", "QJK99AAA", 500m);
        var result = await callbackService.ProcessStkCallbackAsync(payload);
        Assert.Equal("unmatched", result.outcome);
        Assert.Null(result.paymentId);

        var evt = await context.PaymentCallbacks.SingleAsync();
        Assert.False(evt.Processed);
        Assert.Null(evt.PaymentId);
    }
    // ── Wire contract (regression guard) ─────────────────────
    // The documented request value is snake_case ("full_payment"). It used to be
    // rejected because Enum.TryParse cannot bridge "full_payment" → FullPayment,
    // and responses used to emit the separator-less "fullpayment", so a client
    // could not round-trip what the API told it to send.

    [Theory]
    [InlineData("full_payment", PaymentType.FullPayment)]
    [InlineData("booking_fee", PaymentType.BookingFee)]
    [InlineData("balance_payment", PaymentType.BalancePayment)]
    [InlineData("FullPayment", PaymentType.FullPayment)]
    [InlineData("fullpayment", PaymentType.FullPayment)]
    [InlineData("FULL-PAYMENT", PaymentType.FullPayment)]
    [InlineData(" balance payment ", PaymentType.BalancePayment)]
    public void WT1_DocumentedAndLegacySpellings_AllParse(string wire, PaymentType expected)
    {
        Assert.True(PaymentTypes.TryParse(wire, out var parsed));
        Assert.Equal(expected, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("deposit")]
    [InlineData("fullpaymentx")]
    public void WT2_UnknownType_IsRejectedAndNeverDefaulted(string? wire)
    {
        Assert.False(PaymentTypes.TryParse(wire, out _));
    }

    [Fact]
    public void WT3_ResponseAndDtoDefault_StayInLockStepWithThePublishedContract()
    {
        Assert.Equal("full_payment", PaymentTypes.ToWire(PaymentType.FullPayment));
        Assert.Equal("booking_fee", PaymentTypes.ToWire(PaymentType.BookingFee));
        Assert.Equal("balance_payment", PaymentTypes.ToWire(PaymentType.BalancePayment));

        // The DTO default must remain an accepted, full-payment request.
        Assert.True(PaymentTypes.TryParse(new CreatePaymentRequest().Type, out var defaulted));
        Assert.Equal(PaymentType.FullPayment, defaulted);
    }

    [Fact]
    public async Task WT4_CreateWithDocumentedTypeValue_SucceedsAndEchoesSnakeCase()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var (_, _, customer, appointment) = await SeedAsync(context);
        var service = CreatePaymentService(context);

        var created = await service.CreatePaymentAsync(
            new CreatePaymentRequest { AppointmentId = appointment.Id, Method = "cash", Type = "balance_payment" },
            customer.Id, "Customer");

        Assert.True(created.Success);
        Assert.Equal("balance_payment", created.Data!.Type);
    }

    [Fact]
    public async Task CB4_NumericResultCodeInJson_StillAppliesSuccess()
    {
        // Daraja sends "ResultCode": 0 as a JSON NUMBER (not "0"). Reading it as a
        // string-only property left the code empty, so a real success was applied
        // through the FAILURE branch — money moved, payment marked failed.
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var (business, _, _, appointment) = await SeedAsync(context);
        var callbackService = CreateCallbackService(context);
        await SeedStkPaymentAsync(context, business, appointment, "ws_CO_NUMERIC");

        const string raw = """
            {"Body":{"stkCallback":{"MerchantRequestID":"mr-9","CheckoutRequestID":"ws_CO_NUMERIC",
            "ResultCode":0,"ResultDesc":"The service request is processed successfully.",
            "CallbackMetadata":{"Item":[{"Name":"Amount","Value":1000},
            {"Name":"MpesaReceiptNumber","Value":"QJK42NUM"},{"Name":"PhoneNumber","Value":254712345678}]}}}}
            """;

        var result = await callbackService.ProcessStkCallbackAsync(raw);
        Assert.Equal("applied", result.outcome);

        context.ChangeTracker.Clear();
        var stored = await context.Payments.Include(p => p.Attempts).SingleAsync();
        Assert.Equal(PaymentStatus.Success, stored.Status);
        Assert.Equal("QJK42NUM", stored.ExternalReference);
        Assert.Single(await context.PaymentCallbacks.ToListAsync());
    }
}
