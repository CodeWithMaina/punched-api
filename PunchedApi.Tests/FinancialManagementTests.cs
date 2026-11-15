using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;
using PunchedApi.Infrastructure.Repositories;

namespace PunchedApi.Tests;

/// <summary>
/// Standalone income, expenses, refunds, reconciliation, security and overview arithmetic.
///
/// <para>These are the requirements that make Punched a financial tool rather than a booking tool: money
/// that never touched an appointment must be recordable, expenses must be manageable without ever
/// deleting history, and no figure may double-count a payment.</para>
/// </summary>
public class FinancialManagementTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public FinancialManagementTests()
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

    private FinancialService Finances() => new(
        _context, new UnitOfWork(_context), TestHelpers.CreateLogger<FinancialService>());

    private ExpenseService Expenses() => new(
        _context, new UnitOfWork(_context), TestHelpers.CreateLogger<ExpenseService>());

    private FinancialAnalyticsService Analytics() => new(
        _context, TestHelpers.CreateLogger<FinancialAnalyticsService>());

    private MpesaReconciliationService Reconciliation() => new(
        _context, new UnitOfWork(_context), TestHelpers.CreateLogger<MpesaReconciliationService>());

    private FinancialLedgerService Ledger() => new(
        _context, new UnitOfWork(_context), TestHelpers.CreateLogger<FinancialLedgerService>());

    /// <summary>Two independent businesses, so tenant isolation can be asserted directly.</summary>
    private async Task<(Business alpha, User alphaOwner, Business beta, User betaOwner)> SeedTwoBusinessesAsync()
    {
        var alphaOwner = BookingTestBase.CreateOwner("alpha@test.com");
        var alpha = BookingTestBase.CreateBusiness(alphaOwner.Id, "Alpha Salon");

        var betaOwner = BookingTestBase.CreateOwner("beta@test.com");
        var beta = BookingTestBase.CreateBusiness(betaOwner.Id, "Beta Barbers");

        await BookingTestBase.SeedAsync(_context, alphaOwner, alpha, betaOwner, beta);
        return (alpha, alphaOwner, beta, betaOwner);
    }

    private static RecordIncomeRequest IncomeRequest(
        decimal amount, string source = "walk_in", string method = "mpesa_till", string? reference = "QJK42NUM01") =>
        new()
        {
            Amount = amount,
            Source = source,
            PaymentMethod = method,
            TransactionReference = reference,
            Description = "Walk-in haircut"
        };

    // ═══════════════════════════════════════════════════════════
    //  STANDALONE INCOME
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task A_walk_in_till_payment_is_recorded_without_an_appointment_and_stays_pending()
    {
        var (alpha, owner, _, _) = await SeedTwoBusinessesAsync();

        var result = await Finances().RecordIncomeAsync(IncomeRequest(1000m), owner.Id, "Business");

        Assert.True(result.Success);
        var income = result.Data!;
        Assert.Equal("walk_in", income.Source);
        Assert.Equal("mpesa_till", income.PaymentMethod);
        Assert.Equal("QJK42NUM01", income.ExternalReference);
        Assert.Equal("kes", income.Currency.ToLowerInvariant());
        Assert.Null(income.AppointmentId);
        // The owner TYPED the M-Pesa code, so it is a claim awaiting a decision — not verified revenue.
        Assert.Equal("pending", income.VerificationStatus);
        Assert.True(income.IsOwnerRecorded);

        var overview = await Analytics().GetOverviewAsync("last_30_days", null, null, owner.Id, "Business");
        Assert.Equal(0m, overview.Data!.Totals.VerifiedIncome);
        Assert.Equal(1000m, overview.Data.Totals.PendingVerification);
    }

    [Fact]
    public async Task Cash_income_is_recorded_as_verified_because_a_person_handled_the_money()
    {
        var (_, owner, _, _) = await SeedTwoBusinessesAsync();

        var result = await Finances().RecordIncomeAsync(
            IncomeRequest(2500m, source: "product_sale", method: "cash", reference: null), owner.Id, "Business");

        Assert.True(result.Success);
        Assert.Equal("verified", result.Data!.VerificationStatus);
        Assert.Equal("product_sale", result.Data.Source);
    }

    [Fact]
    public async Task Paybill_income_can_be_recorded_without_a_customer()
    {
        var (_, owner, _, _) = await SeedTwoBusinessesAsync();

        var result = await Finances().RecordIncomeAsync(
            IncomeRequest(750m, source: "manual_income", method: "mpesa_paybill", reference: "QJK42NUM02"),
            owner.Id, "Business");

        Assert.True(result.Success);
        Assert.Null(result.Data!.CustomerId);
        Assert.Null(result.Data.CustomerName);
        Assert.Equal("pending", result.Data.VerificationStatus);
    }

    [Fact]
    public async Task An_mpesa_income_row_without_a_reference_is_refused()
    {
        var (_, owner, _, _) = await SeedTwoBusinessesAsync();

        var result = await Finances().RecordIncomeAsync(
            IncomeRequest(1000m, method: "mpesa_till", reference: null), owner.Id, "Business");

        Assert.False(result.Success);
        Assert.Equal("VALIDATION_FAILED", result.Error!.Code);
    }

    [Fact]
    public async Task Booking_income_cannot_be_typed_in_by_hand()
    {
        var (_, owner, _, _) = await SeedTwoBusinessesAsync();

        var result = await Finances().RecordIncomeAsync(
            IncomeRequest(1000m, source: "punched_booking"), owner.Id, "Business");

        Assert.False(result.Success);
        Assert.Contains("automatically", result.Error!.Message);
    }

    [Fact]
    public async Task The_same_mpesa_reference_cannot_be_recorded_twice()
    {
        var (_, owner, _, _) = await SeedTwoBusinessesAsync();

        var first = await Finances().RecordIncomeAsync(IncomeRequest(1000m), owner.Id, "Business");
        var second = await Finances().RecordIncomeAsync(IncomeRequest(1000m), owner.Id, "Business");

        Assert.True(first.Success);
        Assert.False(second.Success);
        Assert.Equal("DUPLICATE_REFERENCE", second.Error!.Code);
        Assert.Equal(1, await _context.FinancialTransactions.CountAsync());
    }

    [Fact]
    public async Task Verifying_a_recorded_claim_turns_it_into_revenue_once()
    {
        var (_, owner, _, _) = await SeedTwoBusinessesAsync();

        var recorded = await Finances().RecordIncomeAsync(IncomeRequest(1000m), owner.Id, "Business");
        Assert.Equal("pending", recorded.Data!.VerificationStatus);

        var verified = await Finances().VerifyTransactionAsync(
            recorded.Data.Id, new VerifyFinancialTransactionRequest { Note = "Confirmed on my phone" },
            owner.Id, "Business");

        Assert.True(verified.Success);
        Assert.Equal("verified", verified.Data!.VerificationStatus);
        Assert.NotNull(verified.Data.VerifiedAt);

        var overview = await Analytics().GetOverviewAsync("last_30_days", null, null, owner.Id, "Business");
        Assert.Equal(1000m, overview.Data!.Totals.VerifiedIncome);
        Assert.Equal(0m, overview.Data.Totals.PendingVerification);

        // Verifying again is idempotent, not a second credit.
        await Finances().VerifyTransactionAsync(recorded.Data.Id, new VerifyFinancialTransactionRequest(), owner.Id, "Business");
        Assert.Equal(1, await _context.FinancialTransactions.CountAsync());
    }

    // ═══════════════════════════════════════════════════════════
    //  EXPENSES
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task An_expense_is_recorded_edited_with_history_and_voided_rather_than_deleted()
    {
        var (_, owner, _, _) = await SeedTwoBusinessesAsync();

        var created = await Expenses().CreateExpenseAsync(new CreateExpenseRequest
        {
            Amount = 15000m,
            Category = "rent",
            PaymentMethod = "bank_transfer",
            Description = "November shop rent",
            TransactionDate = DateTime.UtcNow
        }, owner.Id, "Business");

        Assert.True(created.Success);
        var expenseId = created.Data!.Id;
        Assert.Equal("expense", created.Data.Type);
        Assert.Equal("rent", created.Data.Category);

        var updated = await Expenses().UpdateExpenseAsync(expenseId, new UpdateExpenseRequest
        {
            Amount = 16000m,
            Reason = "Landlord added the service charge"
        }, owner.Id, "Business");

        Assert.True(updated.Success);
        Assert.Equal(16000m, updated.Data!.Amount);

        // The correction is in the trail with BOTH values, not just the new one.
        var audit = await _context.FinancialAuditLogs
            .Where(l => l.EntityId == expenseId && l.Action == "updated")
            .ToListAsync();

        Assert.Single(audit);
        Assert.Contains("16000", audit[0].ChangesJson);
        Assert.Contains("15000", audit[0].ChangesJson);

        var voided = await Expenses().VoidExpenseAsync(expenseId,
            new VoidFinancialRecordRequest { Reason = "Paid from the owner's personal account" }, owner.Id, "Business");

        Assert.True(voided.Success);
        Assert.True(voided.Data!.IsVoided);

        // Still a row, still readable — just not counted.
        var row = await _context.FinancialTransactions.SingleAsync(t => t.Id == expenseId);
        Assert.True(row.IsVoided);
        Assert.NotNull(row.VoidReason);

        var overview = await Analytics().GetOverviewAsync("last_30_days", null, null, owner.Id, "Business");
        Assert.Equal(0m, overview.Data!.Totals.TotalExpenses);
    }

    [Fact]
    public async Task An_expense_with_an_unknown_category_is_refused()
    {
        var (_, owner, _, _) = await SeedTwoBusinessesAsync();

        var result = await Expenses().CreateExpenseAsync(new CreateExpenseRequest
        {
            Amount = 100m,
            Category = "bribes",
            PaymentMethod = "cash",
            Description = "Nope"
        }, owner.Id, "Business");

        Assert.False(result.Success);
        Assert.Equal("VALIDATION_FAILED", result.Error!.Code);
    }

    [Fact]
    public async Task The_cashbook_nets_recorded_inflows_against_expenses_and_completed_refunds()
    {
        var (_, owner, _, _) = await SeedTwoBusinessesAsync();

        await Finances().RecordIncomeAsync(
            IncomeRequest(3000m, method: "cash", reference: null), owner.Id, "Business");

        await Expenses().CreateExpenseAsync(new CreateExpenseRequest
        {
            Amount = 500m,
            Category = "utilities",
            PaymentMethod = "cash",
            Description = "Water"
        }, owner.Id, "Business");

        var book = await Analytics().GetCashbookAsync(new FinancialTransactionQuery(), owner.Id, "Business");

        Assert.True(book.Success);
        Assert.Equal(3000m, book.Data!.TotalInflows);
        Assert.Equal(500m, book.Data.TotalOutflows);
        Assert.Equal(2500m, book.Data.NetCashMovement);
        Assert.Equal(2, book.Data.TotalCount);
        // The direction is the server's own classification, in its canonical wire spelling.
        Assert.Contains(book.Data.Entries, e => e.Direction == "incoming" && e.Type == "income");
        Assert.Contains(book.Data.Entries, e => e.Direction == "outgoing" && e.Type == "expense");
    }

    // ═══════════════════════════════════════════════════════════
    //  REFUNDS
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task A_partial_refund_is_linked_to_its_income_and_never_rewrites_it()
    {
        var (_, owner, _, _) = await SeedTwoBusinessesAsync();

        var income = await Finances().RecordIncomeAsync(
            IncomeRequest(2000m, method: "cash", reference: null), owner.Id, "Business");

        var refund = await Finances().RecordRefundAsync(new RecordRefundRequest
        {
            TransactionId = income.Data!.Id,
            Amount = 800m,
            Reason = "Service not completed as agreed",
            RefundMethod = "mpesa_send_money",
            RefundReference = "QJK42NUM09",
            Completed = true
        }, owner.Id, "Business");

        Assert.True(refund.Success);
        Assert.Equal("refund", refund.Data!.Type);
        Assert.Equal(income.Data.Id, refund.Data.RefundOfTransactionId);
        Assert.Equal("verified", refund.Data.VerificationStatus);

        var original = await _context.FinancialTransactions.SingleAsync(t => t.Id == income.Data.Id);
        Assert.Equal(2000m, original.Amount);
        Assert.Equal(FinancialVerificationStatus.Verified, original.VerificationStatus);

        var overview = await Analytics().GetOverviewAsync("last_30_days", null, null, owner.Id, "Business");
        Assert.Equal(2000m, overview.Data!.Totals.VerifiedIncome);
        Assert.Equal(800m, overview.Data.Totals.TotalRefunds);
        Assert.Equal(1200m, overview.Data.Totals.NetCashMovement);

        // What is left to refund is reported, so the screen can cap the next attempt.
        var refreshed = await Finances().GetTransactionAsync(income.Data.Id, owner.Id, "Business");
        Assert.Equal(1200m, refreshed.Data!.RefundableAmount);
    }

    [Fact]
    public async Task A_refund_beyond_what_is_left_is_refused()
    {
        var (_, owner, _, _) = await SeedTwoBusinessesAsync();

        var income = await Finances().RecordIncomeAsync(
            IncomeRequest(1000m, method: "cash", reference: null), owner.Id, "Business");

        var result = await Finances().RecordRefundAsync(new RecordRefundRequest
        {
            TransactionId = income.Data!.Id,
            Amount = 1500m,
            Reason = "Too much",
            RefundMethod = "cash",
            Completed = true
        }, owner.Id, "Business");

        Assert.False(result.Success);
        Assert.Equal("REFUND_EXCEEDS_AVAILABLE", result.Error!.Code);
    }

    [Fact]
    public async Task A_requested_refund_stays_pending_and_does_not_reduce_the_net_until_completed()
    {
        var (_, owner, _, _) = await SeedTwoBusinessesAsync();

        var income = await Finances().RecordIncomeAsync(
            IncomeRequest(1000m, method: "cash", reference: null), owner.Id, "Business");

        var requested = await Finances().RecordRefundAsync(new RecordRefundRequest
        {
            TransactionId = income.Data!.Id,
            Amount = 400m,
            Reason = "Customer asked for a refund",
            RefundMethod = "cash",
            Completed = false
        }, owner.Id, "Business");

        Assert.True(requested.Success);
        Assert.Equal("pending", requested.Data!.VerificationStatus);

        var overview = await Analytics().GetOverviewAsync("last_30_days", null, null, owner.Id, "Business");
        Assert.Equal(0m, overview.Data!.Totals.TotalRefunds);
        Assert.Equal(1000m, overview.Data.Totals.NetCashMovement);
    }

    // ═══════════════════════════════════════════════════════════
    //  SECURITY AND TENANT ISOLATION
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task One_business_never_sees_another_business_transactions()
    {
        var (alpha, alphaOwner, _, betaOwner) = await SeedTwoBusinessesAsync();

        var alphaIncome = await Finances().RecordIncomeAsync(
            IncomeRequest(1000m, method: "cash", reference: null), alphaOwner.Id, "Business");
        Assert.True(alphaIncome.Success);
        Assert.Equal(alpha.Id, alphaIncome.Data!.BusinessId);

        // Beta's ledger is empty and Beta cannot reach Alpha's row by id.
        var betaList = await Finances().ListTransactionsAsync(new FinancialTransactionQuery(), betaOwner.Id, "Business");
        Assert.True(betaList.Success);
        Assert.Empty(betaList.Data!.Items);

        var stolen = await Finances().GetTransactionAsync(alphaIncome.Data.Id, betaOwner.Id, "Business");
        Assert.False(stolen.Success);
        Assert.Equal("NOT_FOUND", stolen.Error!.Code);

        // Nor can Beta verify Alpha's claim.
        var attempted = await Finances().VerifyTransactionAsync(
            alphaIncome.Data.Id, new VerifyFinancialTransactionRequest(), betaOwner.Id, "Business");
        Assert.False(attempted.Success);
        Assert.Equal("NOT_FOUND", attempted.Error!.Code);
    }

    [Fact]
    public async Task A_request_cannot_attach_another_business_customer_to_its_ledger()
    {
        var (_, alphaOwner, _, betaOwner) = await SeedTwoBusinessesAsync();

        // A customer enrolled with Alpha only.
        var customer = BookingTestBase.CreateCustomer("shared@test.com");
        await BookingTestBase.SeedAsync(_context, customer);
        await BookingTestBase.SeedAsync(_context, new CustomerBusinessEnrollment
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            BusinessId = (await _context.BusinessMemberships
                .Where(m => m.UserId == alphaOwner.Id)
                .Select(m => m.BusinessId)
                .FirstAsync()),
            Status = CustomerBusinessEnrollmentStatus.Active,
            Source = "discovery",
            EnrolledAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });

        var request = IncomeRequest(1000m, method: "cash", reference: null);
        request.CustomerId = customer.Id;

        var result = await Finances().RecordIncomeAsync(request, betaOwner.Id, "Business");

        Assert.False(result.Success);
        Assert.Equal("NOT_FOUND", result.Error!.Code);
    }

    [Fact]
    public async Task A_user_with_no_business_membership_cannot_touch_the_ledger()
    {
        await SeedTwoBusinessesAsync();

        var outsider = BookingTestBase.CreateCustomer("outsider@test.com");
        await BookingTestBase.SeedAsync(_context, outsider);

        var result = await Finances().RecordIncomeAsync(
            IncomeRequest(1000m, method: "cash", reference: null), outsider.Id, "Customer");

        Assert.False(result.Success);
        Assert.Equal("FORBIDDEN", result.Error!.Code);
        Assert.Equal(0, await _context.FinancialTransactions.CountAsync());
    }
}
