using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;
using PunchedApi.Infrastructure.Repositories;

namespace PunchedApi.Tests;

/// <summary>
/// Financial analytics: period windowing, verified-only revenue, no double counting, expenses,
/// outstanding balances, breakdown arithmetic and tenant isolation.
///
/// <para>These assertions are about money, so each one names the figure it protects: if a change makes
/// pending money count as revenue, or lets a payment be recognised twice, one of these fails.</para>
/// </summary>
public class FinancialAnalyticsTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public FinancialAnalyticsTests()
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

    private FinancialService Finances() => new(
        _context, new UnitOfWork(_context), TestHelpers.CreateLogger<FinancialService>());

    private ExpenseService Expenses() => new(
        _context, new UnitOfWork(_context), TestHelpers.CreateLogger<ExpenseService>());

    private FinancialAnalyticsService Analytics() => new(
        _context, TestHelpers.CreateLogger<FinancialAnalyticsService>());

    private FinancialLedgerService Ledger() => new(
        _context, new UnitOfWork(_context), TestHelpers.CreateLogger<FinancialLedgerService>());

    /// <summary>A business with an owner and nothing else — the empty-book baseline.</summary>
    private async Task<(Business business, User owner)> SeedBusinessAsync(string email = "owner@test.com")
    {
        var owner = BookingTestBase.CreateOwner(email);
        var business = BookingTestBase.CreateBusiness(owner.Id, "Test Business");
        await BookingTestBase.SeedAsync(_context, owner, business);
        return (business, owner);
    }

    /// <summary>Records money the business took, through the real standalone-income path.</summary>
    private async Task<Guid> RecordIncomeAsync(
        User owner, decimal amount, string method = "cash", string? reference = null, DateTime? when = null)
    {
        var result = await Finances().RecordIncomeAsync(new RecordIncomeRequest
        {
            Amount = amount,
            Source = "walk_in",
            PaymentMethod = method,
            TransactionReference = reference,
            Description = "Walk-in",
            TransactionDate = when ?? DateTime.UtcNow
        }, owner.Id, "Business");

        Assert.True(result.Success, result.Error?.Message);
        return result.Data!.Id;
    }

    private async Task<FinancialAnalyticsResponse> AnalyticsAsync(User owner, string period,
        DateTime? from = null, DateTime? to = null)
    {
        var result = await Analytics().GetAnalyticsAsync(period, from, to, owner.Id, "Business");
        Assert.True(result.Success, result.Error?.Message);
        return result.Data!;
    }

    // ═══════════════════════════════════════════════════════════
    //  DATE FILTERING
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task A_period_window_counts_only_the_money_recorded_inside_it()
    {
        var (_, owner) = await SeedBusinessAsync();

        await RecordIncomeAsync(owner, 1000m);
        await RecordIncomeAsync(owner, 9000m, when: DateTime.UtcNow.AddDays(-10));

        var week = await AnalyticsAsync(owner, "last_7_days");
        var month = await AnalyticsAsync(owner, "last_30_days");

        Assert.Equal(1000m, week.Performance.TotalIncome);
        Assert.Equal(1000m, week.Revenue.OverTime.Sum(p => p.Amount));

        Assert.Equal(10000m, month.Performance.TotalIncome);
    }

    [Fact]
    public async Task A_custom_range_includes_both_of_its_ends()
    {
        var (_, owner) = await SeedBusinessAsync();

        // The window is [from, to] as a person reading a date picker expects.
        var dayStart = DateTime.UtcNow.Date.AddDays(-3);
        var inside = dayStart.AddHours(9);
        var lastDayInside = DateTime.UtcNow.Date.AddHours(23);
        var before = dayStart.AddHours(-1);

        await RecordIncomeAsync(owner, 500m, when: inside);
        await RecordIncomeAsync(owner, 700m, when: lastDayInside);
        await RecordIncomeAsync(owner, 100_000m, when: before);

        var custom = await AnalyticsAsync(owner, "custom", from: dayStart, to: DateTime.UtcNow.Date);

        Assert.Equal("custom", custom.Period);
        Assert.Equal(1200m, custom.Performance.TotalIncome);
    }

    // ═══════════════════════════════════════════════════════════
    //  VERIFIED-ONLY REVENUE AND NO DOUBLE COUNTING
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task A_settled_payment_is_counted_once_even_when_the_ledger_write_is_replayed()
    {
        var (business, owner) = await SeedBusinessAsync();

        var customer = BookingTestBase.CreateCustomer();
        await BookingTestBase.SeedAsync(_context, customer);

        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            CustomerId = customer.Id,
            Type = PaymentType.FullPayment,
            Method = PaymentMethod.Mpesa,
            Provider = PaymentProviderKind.DarajaC2B,
            Status = PaymentStatus.Success,
            Amount = 2500m,
            Currency = "KES",
            Reference = "PMT-ANALYTICS-01",
            CompletedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        };

        await BookingTestBase.SeedAsync(_context, payment);

        // The live path can fire more than once for one payment; the ledger is what guarantees it lands once.
        await Ledger().EnsureIncomeForPaymentAsync(payment);
        await Ledger().EnsureIncomeForPaymentAsync(payment);

        Assert.Equal(1, await _context.FinancialTransactions.CountAsync(t => t.SourcePaymentId == payment.Id));

        var analytics = await AnalyticsAsync(owner, "last_30_days");

        Assert.Equal(2500m, analytics.Performance.TotalIncome);
        Assert.Equal(1, analytics.Payments.VerifiedCount);
        Assert.Equal(2500m, analytics.Payments.VerifiedAmount);
    }

    [Fact]
    public async Task Pending_money_is_reported_as_pending_and_never_as_revenue()
    {
        var (_, owner) = await SeedBusinessAsync();

        // Cash is verified (a person handled the money); an M-Pesa claim the owner typed is not.
        await RecordIncomeAsync(owner, 300m);
        await RecordIncomeAsync(owner, 500m, method: "mpesa_till", reference: "QJK42NUM02");

        var analytics = await AnalyticsAsync(owner, "last_30_days");

        Assert.Equal(300m, analytics.Performance.TotalIncome);
        Assert.Equal(300m, analytics.Payments.VerifiedAmount);
        Assert.Equal(1, analytics.Payments.VerifiedCount);
        Assert.Equal(500m, analytics.Payments.PendingAmount);
        Assert.Equal(1, analytics.Payments.PendingCount);
        Assert.Equal(0m, analytics.Payments.RejectedAmount);
    }

    [Fact]
    public async Task Rejected_money_is_neither_revenue_nor_pending()
    {
        var (_, owner) = await SeedBusinessAsync();

        var claim = await RecordIncomeAsync(owner, 700m, method: "mpesa_paybill", reference: "QJK42NUM03");

        var rejected = await Finances().RejectTransactionAsync(
            claim, new RejectFinancialTransactionRequest { Reason = "Never reached the paybill" },
            owner.Id, "Business");
        Assert.True(rejected.Success, rejected.Error?.Message);

        var analytics = await AnalyticsAsync(owner, "last_30_days");

        Assert.Equal(0m, analytics.Performance.TotalIncome);
        Assert.Equal(0m, analytics.Payments.PendingAmount);
        Assert.Equal(700m, analytics.Payments.RejectedAmount);
        Assert.Equal(1, analytics.Payments.RejectedCount);
    }

    [Fact]
    public async Task The_breakdowns_account_for_every_shilling_of_the_periods_income()
    {
        var (_, owner) = await SeedBusinessAsync();

        await RecordIncomeAsync(owner, 1200m);
        await RecordIncomeAsync(owner, 800m, method: "bank_transfer");
        await RecordIncomeAsync(owner, 500m, method: "card");

        var analytics = await AnalyticsAsync(owner, "last_30_days");
        var total = analytics.Performance.TotalIncome;

        Assert.Equal(2500m, total);
        Assert.Equal(total, analytics.Revenue.BySource.Sum(item => item.Amount));
        Assert.Equal(total, analytics.Revenue.ByPaymentMethod.Sum(item => item.Amount));
        // The biggest contributor is first, so the answer needs no comparing of rows by hand.
        Assert.Equal("cash", analytics.Revenue.ByPaymentMethod[0].Key);
        Assert.Equal(833.33m, analytics.Revenue.AverageTransactionValue);
    }

    // ═══════════════════════════════════════════════════════════
    //  EXPENSES
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Expenses_are_totalled_by_category_and_a_voided_expense_stops_counting()
    {
        var (_, owner) = await SeedBusinessAsync();

        await Expenses().CreateExpenseAsync(new CreateExpenseRequest
        {
            Amount = 15000m, Category = "rent", PaymentMethod = "bank_transfer",
            Description = "Shop rent", TransactionDate = DateTime.UtcNow
        }, owner.Id, "Business");

        var utilities = await Expenses().CreateExpenseAsync(new CreateExpenseRequest
        {
            Amount = 2000m, Category = "utilities", PaymentMethod = "cash",
            Description = "Water bill", TransactionDate = DateTime.UtcNow
        }, owner.Id, "Business");

        await Expenses().VoidExpenseAsync(utilities.Data!.Id,
            new VoidFinancialRecordRequest { Reason = "Paid personally" }, owner.Id, "Business");

        var analytics = await AnalyticsAsync(owner, "last_30_days");

        Assert.Equal(15000m, analytics.Expenses.Total);
        Assert.Equal(1, analytics.Expenses.Count);
        Assert.Equal(15000m, analytics.Performance.TotalExpenses);
        Assert.Single(analytics.Expenses.ByCategory);
        Assert.Equal("rent", analytics.Expenses.ByCategory[0].Key);
        Assert.Equal(15000m, analytics.Expenses.ByCategory[0].Amount);
    }

    [Fact]
    public async Task The_net_operating_result_carries_expenses_and_completed_refunds_as_outgoings()
    {
        var (_, owner) = await SeedBusinessAsync();

        var income = await RecordIncomeAsync(owner, 10000m);

        await Expenses().CreateExpenseAsync(new CreateExpenseRequest
        {
            Amount = 3000m, Category = "stock_and_supplies", PaymentMethod = "cash",
            Description = "Restock", TransactionDate = DateTime.UtcNow
        }, owner.Id, "Business");

        await Finances().RecordRefundAsync(new RecordRefundRequest
        {
            TransactionId = income, Amount = 1000m, Reason = "Client unhappy with the cut",
            RefundMethod = "cash", Completed = true, RefundDate = DateTime.UtcNow
        }, owner.Id, "Business");

        var analytics = await AnalyticsAsync(owner, "last_30_days");

        Assert.Equal(10000m, analytics.Performance.TotalIncome);
        Assert.Equal(3000m, analytics.Performance.TotalExpenses);
        Assert.Equal(1000m, analytics.Performance.TotalRefunds);
        Assert.Equal(6000m, analytics.Performance.NetOperatingResult);
        Assert.Equal(10m, analytics.Performance.RefundRate);
        Assert.Equal(60m, analytics.Performance.MarginPercentage);
    }

    // ═══════════════════════════════════════════════════════════
    //  OUTSTANDING BALANCES
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Outstanding_balances_come_from_appointment_obligations_and_ignore_other_income()
    {
        var (business, owner) = await SeedBusinessAsync();

        var customer = BookingTestBase.CreateCustomer();
        await BookingTestBase.SeedAsync(_context, customer);

        var appointment = await SeedAppointmentAsync(business, customer, 1000m);
        await SeedSettledPaymentAsync(business, customer, appointment, 400m);

        // Money that has nothing to do with this appointment must not reduce what the visit still owes.
        await RecordIncomeAsync(owner, 5000m);

        var analytics = await AnalyticsAsync(owner, "last_30_days");

        Assert.Equal(600m, analytics.Performance.OutstandingBalances);
        Assert.Equal(1, analytics.Performance.OutstandingAppointmentCount);
    }

    [Fact]
    public async Task A_completed_refund_reopens_the_appointment_balance_it_reversed()
    {
        var (business, owner) = await SeedBusinessAsync();

        var customer = BookingTestBase.CreateCustomer();
        await BookingTestBase.SeedAsync(_context, customer);

        var appointment = await SeedAppointmentAsync(business, customer, 1000m);
        var payment = await SeedSettledPaymentAsync(business, customer, appointment, 1000m);

        var income = await Ledger().EnsureIncomeForPaymentAsync(payment);
        Assert.NotNull(income);

        // Fully paid, so nothing is outstanding — until part of the money goes back.
        var before = await AnalyticsAsync(owner, "last_30_days");
        Assert.Equal(0m, before.Performance.OutstandingBalances);

        var refund = await Finances().RecordRefundAsync(new RecordRefundRequest
        {
            TransactionId = income!.Id, Amount = 300m, Reason = "Partial refund agreed",
            RefundMethod = "mpesa_send_money", RefundReference = "QJK42NUM77",
            Completed = true, RefundDate = DateTime.UtcNow
        }, owner.Id, "Business");

        Assert.True(refund.Success, refund.Error?.Message);

        var after = await AnalyticsAsync(owner, "last_30_days");
        Assert.Equal(300m, after.Performance.OutstandingBalances);
        Assert.Equal(300m, after.Performance.TotalRefunds);
    }

    // ═══════════════════════════════════════════════════════════
    //  TENANT ISOLATION
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task One_business_never_sees_another_business_figures()
    {
        var (_, alphaOwner) = await SeedBusinessAsync("analytics-alpha@test.com");
        var (_, betaOwner) = await SeedBusinessAsync("analytics-beta@test.com");

        await RecordIncomeAsync(alphaOwner, 4000m);
        await RecordIncomeAsync(betaOwner, 700m);

        await Expenses().CreateExpenseAsync(new CreateExpenseRequest
        {
            Amount = 250m, Category = "transport", PaymentMethod = "cash",
            Description = "Alpha's fuel", TransactionDate = DateTime.UtcNow
        }, alphaOwner.Id, "Business");

        var alpha = await AnalyticsAsync(alphaOwner, "last_30_days");
        var beta = await AnalyticsAsync(betaOwner, "last_30_days");

        Assert.Equal(4000m, alpha.Performance.TotalIncome);
        Assert.Equal(250m, alpha.Performance.TotalExpenses);

        Assert.Equal(700m, beta.Performance.TotalIncome);
        Assert.Equal(0m, beta.Performance.TotalExpenses);
    }

    // ── Appointment + payment builders ─────────────────────────

    private async Task<Appointment> SeedAppointmentAsync(Business business, User customer, decimal price)
    {
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
            Price = price,
            CreatedAt = DateTime.UtcNow
        });

        await BookingTestBase.SeedAsync(_context, appointment);
        return appointment;
    }

    /// <summary>A payment the provider already settled, which is what releases the ledger income.</summary>
    private async Task<Payment> SeedSettledPaymentAsync(
        Business business, User customer, Appointment appointment, decimal amount)
    {
        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            CustomerId = customer.Id,
            AppointmentId = appointment.Id,
            Type = PaymentType.BalancePayment,
            Method = PaymentMethod.Mpesa,
            Provider = PaymentProviderKind.DarajaC2B,
            Status = PaymentStatus.Success,
            Amount = amount,
            Currency = "KES",
            Reference = "PMT-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant(),
            CompletedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        };

        await BookingTestBase.SeedAsync(_context, payment);
        return payment;
    }

}
