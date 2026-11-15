using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
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
/// Financial management tests: the ledger's integration with the payment lifecycle, standalone income,
/// expenses, refunds and the guarantees that must never break — one row per settled payment, nothing
/// pending counted as revenue, and no cross-tenant access.
/// </summary>
public class FinancialLedgerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public FinancialLedgerTests()
    {
        _connection = BookingTestBase.CreateConnection();
        _context = BookingTestBase.CreateContext(_connection);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    // ── Harness ────────────────────────────────────────────────

    private FinancialLedgerService Ledger() => new(
        _context, new UnitOfWork(_context), TestHelpers.CreateLogger<FinancialLedgerService>());

    private FinancialService Finances() => new(
        _context, new UnitOfWork(_context), TestHelpers.CreateLogger<FinancialService>());

    private ExpenseService Expenses() => new(
        _context, new UnitOfWork(_context), TestHelpers.CreateLogger<ExpenseService>());

    private FinancialAnalyticsService Analytics() => new(
        _context, TestHelpers.CreateLogger<FinancialAnalyticsService>());

    private MpesaReconciliationService Reconciliation() => new(
        _context, new UnitOfWork(_context), TestHelpers.CreateLogger<MpesaReconciliationService>());

    private PaymentService Payments()
    {
        var env = new Moq.Mock<Microsoft.Extensions.Hosting.IHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns("Development");

        return new PaymentService(
            _context,
            new UnitOfWork(_context),
            new IPaymentProvider[] { new CashPaymentProvider(TestHelpers.CreateLogger<CashPaymentProvider>()) },
            new PermissionService(),
            new PaymentCredentialProtector(Options.Create(new PaymentOptions()), env.Object),
            Options.Create(new PaymentOptions()),
            BookingTestBase.CreateNotificationService(_context),
            TestHelpers.CreateLogger<PaymentService>(),
            commissions: null,
            ledger: Ledger());
    }

    /// <summary>A business with an owner, a customer and a 1,000 KES appointment.</summary>
    private async Task<(Business business, User owner, User customer, Appointment appointment)> SeedAsync()
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

        await BookingTestBase.SeedAsync(_context, owner, business, customer, appointment);

        // The customer is enrolled, so the notification stack treats them as associated.
        await BookingTestBase.SeedAsync(_context, new CustomerBusinessEnrollment
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            BusinessId = business.Id,
            Status = CustomerBusinessEnrollmentStatus.Active,
            Source = "discovery",
            EnrolledAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        return (business, owner, customer, appointment);
    }

    private async Task<Payment> SeedPaymentAsync(
        Business business, User customer, Appointment? appointment,
        decimal amount, PaymentStatus status, PaymentProviderKind provider = PaymentProviderKind.Cash,
        string? receipt = null)
    {
        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            CustomerId = customer.Id,
            AppointmentId = appointment?.Id,
            Type = appointment == null ? PaymentType.FullPayment : PaymentType.BalancePayment,
            Method = provider == PaymentProviderKind.Cash ? PaymentMethod.Cash : PaymentMethod.Mpesa,
            Provider = provider,
            Status = status,
            Amount = amount,
            Currency = "KES",
            Reference = "PMT-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant(),
            CompletedAt = status == PaymentStatus.Success ? DateTime.UtcNow : null,
            VerifiedAt = status == PaymentStatus.Verified ? DateTime.UtcNow : null,
            ExternalReference = receipt,
            CreatedAt = DateTime.UtcNow
        };

        await BookingTestBase.SeedAsync(_context, payment);
        return payment;
    }

    // ═══════════════════════════════════════════════════════════
    //  PAYMENT INTEGRATION
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task A_settled_payment_is_recognised_exactly_once_however_often_it_is_replayed()
    {
        var (business, _, customer, appointment) = await SeedAsync();
        var payment = await SeedPaymentAsync(business, customer, appointment, 1000m, PaymentStatus.Success);

        var first = await Ledger().EnsureIncomeForPaymentAsync(payment);
        var second = await Ledger().EnsureIncomeForPaymentAsync(payment);

        Assert.NotNull(first);
        Assert.Equal(first!.Id, second!.Id);
        Assert.Equal(1, await _context.FinancialTransactions.CountAsync(t => t.SourcePaymentId == payment.Id));

        var income = await _context.FinancialTransactions.SingleAsync(t => t.SourcePaymentId == payment.Id);
        Assert.Equal(FinancialTransactionType.Income, income.Type);
        Assert.Equal(TransactionSource.PunchedBooking, income.Source);
        Assert.Equal(FinancialVerificationStatus.Verified, income.VerificationStatus);
        // A verified payment is NOT reconciled: reconciliation is a separate, later comparison.
        Assert.Equal(ReconciliationStatus.Unreconciled, income.ReconciliationStatus);
        Assert.Equal(1000m, income.Amount);
        Assert.Equal(appointment.Id, income.AppointmentId);
        Assert.Equal("Test Customer", income.CustomerName);
    }

    [Fact]
    public async Task A_pending_or_rejected_payment_produces_no_ledger_row_but_still_shows_as_pending()
    {
        var (business, owner, customer, appointment) = await SeedAsync();
        _ = business;
        var submitted = await SeedPaymentAsync(business, customer, appointment, 500m, PaymentStatus.Submitted);
        var rejected = await SeedPaymentAsync(business, customer, appointment, 500m, PaymentStatus.Rejected);
        var awaiting = await SeedPaymentAsync(business, customer, appointment, 500m, PaymentStatus.AwaitingCustomer);

        Assert.Null(await Ledger().EnsureIncomeForPaymentAsync(submitted));
        Assert.Null(await Ledger().EnsureIncomeForPaymentAsync(rejected));
        Assert.Null(await Ledger().EnsureIncomeForPaymentAsync(awaiting));

        Assert.Equal(0, await _context.FinancialTransactions.CountAsync());

        // Their money is still visible — as PENDING, never as revenue. The rejected payment is NOT part
        // of it: a refusal is a decision, not money still expected, so only the submitted and
        // awaiting-customer amounts count.
        var overview = await Analytics().GetOverviewAsync("last_30_days", null, null, owner.Id, "Business");
        Assert.True(overview.Success);
        Assert.Equal(0m, overview.Data!.Totals.VerifiedIncome);
        Assert.Equal(1000m, overview.Data.Totals.PendingVerification);
    }

    [Fact]
    public async Task Cash_confirmation_recognises_the_income_through_the_payment_service()
    {
        var (business, owner, _, appointment) = await SeedAsync();
        _ = business;

        var created = await Payments().CreatePaymentAsync(new CreatePaymentRequest
        {
            AppointmentId = appointment.Id,
            Method = "cash",
            Type = "balance_payment"
        }, owner.Id, "Business");

        Assert.True(created.Success);

        var confirmed = await Payments().ConfirmCashAsync(created.Data!.Id, "paid at the desk", owner.Id, "Business");
        Assert.True(confirmed.Success);

        var income = await _context.FinancialTransactions.SingleAsync();
        Assert.Equal(1000m, income.Amount);
        Assert.Equal(FinancialPaymentMethod.Cash, income.PaymentMethod);
        Assert.Equal(confirmed.Data!.Id, income.SourcePaymentId);

        // Confirming again must not create a second row.
        await Payments().ConfirmCashAsync(created.Data.Id, null, owner.Id, "Business");
        Assert.Equal(1, await _context.FinancialTransactions.CountAsync());
    }
}
