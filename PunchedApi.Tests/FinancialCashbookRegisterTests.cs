using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;
using PunchedApi.Infrastructure.Repositories;

namespace PunchedApi.Tests;

/// <summary>
/// The Cashbook register and the expense register — the two places a person actually READS the
/// books.
///
/// <para>The Cashbook is the master financial book: every recognised transaction in, every active
/// expense out, in one chronological record. These tests pin the properties that make it
/// trustworthy — the running balance is chronological and starts at zero rather than at an
/// invented opening balance, a voided expense stops counting while staying visible, and the
/// expense register's month total does not move with the column filters.</para>
/// </summary>
public class FinancialCashbookRegisterTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public FinancialCashbookRegisterTests()
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

    private async Task<(Business business, User owner)> SeedAsync()
    {
        var owner = BookingTestBase.CreateOwner("register@test.com");
        var business = BookingTestBase.CreateBusiness(owner.Id, "Register Salon");
        await BookingTestBase.SeedAsync(_context, owner, business);
        return (business, owner);
    }

    private static DateTime DaysAgo(int days) => DateTime.UtcNow.AddDays(-days);

    /// <summary>Cash is verified on record — a person had the money in hand — so it is recognised income.</summary>
    private Task<ApiResponse<FinancialTransactionResponse>> RecordCashIncomeAsync(
        Guid ownerId, decimal amount, DateTime when) =>
        Finances().RecordIncomeAsync(new RecordIncomeRequest
        {
            Amount = amount,
            Source = "walk_in",
            PaymentMethod = "cash",
            Description = "Walk-in haircut",
            TransactionDate = when
        }, ownerId, "Business");

    private Task<ApiResponse<FinancialTransactionResponse>> RecordExpenseAsync(
        Guid ownerId, decimal amount, DateTime when, string description = "Shop rent") =>
        Expenses().CreateExpenseAsync(new CreateExpenseRequest
        {
            Amount = amount,
            Category = ExpenseCategories.Rent,
            PaymentMethod = "cash",
            Description = description,
            TransactionDate = when
        }, ownerId, "Business");

    private Task<ApiResponse<CashbookResponse>> CashbookAsync(Guid ownerId) =>
        Analytics().GetCashbookAsync(new FinancialTransactionQuery
        {
            From = DaysAgo(10),
            To = DateTime.UtcNow,
            PageSize = 50
        }, ownerId, "Business");

    private Task<ApiResponse<FinancialTransactionListResponse>> ExpenseRegisterAsync(Guid ownerId) =>
        Expenses().ListExpensesAsync(new FinancialTransactionQuery { PageSize = 50 }, ownerId, "Business");

    [Fact]
    public async Task The_cashbook_runs_a_balance_down_the_register_and_names_who_recorded_each_entry()
    {
        var (_, owner) = await SeedAsync();

        Assert.True((await RecordCashIncomeAsync(owner.Id, 2500m, DaysAgo(2))).Success);
        Assert.True((await RecordExpenseAsync(owner.Id, 1500m, DaysAgo(1), "Stock purchase")).Success);

        var result = await CashbookAsync(owner.Id);
        Assert.True(result.Success);

        var book = result.Data!;
        Assert.Equal(2500m, book.TotalInflows);
        Assert.Equal(1500m, book.TotalOutflows);
        Assert.Equal(1000m, book.NetCashMovement);

        // Newest first, so the expense sits on top. Its running balance is the whole window's net
        // (2,500 in − 1,500 out); the older income carries the balance as it stood after it.
        Assert.Equal(2, book.Entries.Count);

        var newest = book.Entries[0];
        var oldest = book.Entries[1];

        Assert.Equal("expense", newest.Type);
        Assert.Equal("outgoing", newest.Direction);
        Assert.Equal(1000m, newest.RunningBalance);

        Assert.Equal("income", oldest.Type);
        Assert.Equal("incoming", oldest.Direction);
        Assert.Equal(2500m, oldest.RunningBalance);

        // The ledger stores only an id for the actor, so this proves the register's single-query
        // name resolution actually ran rather than leaving a column of ids.
        Assert.All(book.Entries, entry => Assert.Equal("Test Owner", entry.RecordedByName));
    }

    [Fact]
    public async Task A_voided_expense_leaves_the_cashbook_and_the_month_total_but_stays_in_the_register()
    {
        var (_, owner) = await SeedAsync();

        Assert.True((await RecordCashIncomeAsync(owner.Id, 5000m, DaysAgo(1))).Success);
        var expense = await RecordExpenseAsync(owner.Id, 3000m, DateTime.UtcNow, "Duplicate rent");
        Assert.True(expense.Success);

        var before = (await CashbookAsync(owner.Id)).Data!;
        Assert.Equal(3000m, before.TotalOutflows);
        Assert.Equal(2000m, before.NetCashMovement);

        var voided = await Expenses().VoidExpenseAsync(
            expense.Data!.Id, new VoidFinancialRecordRequest { Reason = "Duplicate entry" }, owner.Id, "Business");
        Assert.True(voided.Success);
        Assert.True(voided.Data!.IsVoided);

        var after = (await CashbookAsync(owner.Id)).Data!;
        Assert.Equal(0m, after.TotalOutflows);
        Assert.Equal(0m, after.Expenses);
        Assert.Equal(5000m, after.NetCashMovement);

        // And it is still there: voiding removes the figure from the totals, never the evidence
        // that the money was once recorded.
        var register = (await ExpenseRegisterAsync(owner.Id)).Data!;
        Assert.Contains(register.Items, row => row.Id == expense.Data.Id && row.IsVoided);
        Assert.Equal(0m, register.ExpenseTotal);
        Assert.Equal(0m, register.CurrentMonthExpenseTotal);
    }

    [Fact]
    public async Task The_expense_register_reports_the_calendar_month_separately_from_the_all_time_total()
    {
        var (_, owner) = await SeedAsync();

        Assert.True((await RecordExpenseAsync(owner.Id, 1000m, DateTime.UtcNow, "Stock")).Success);
        Assert.True((await RecordExpenseAsync(owner.Id, 500m, DateTime.UtcNow, "Transport")).Success);
        Assert.True((await RecordExpenseAsync(owner.Id, 700m, DaysAgo(90), "Last quarter rent")).Success);

        var register = (await ExpenseRegisterAsync(owner.Id)).Data!;

        // All three count towards the book; only this month's two count towards the month.
        Assert.Equal(2200m, register.ExpenseTotal);
        Assert.Equal(1500m, register.CurrentMonthExpenseTotal);
        Assert.Equal(3, register.TotalCount);
        Assert.All(register.Items, row => Assert.Equal("Test Owner", row.CreatedByName));
    }

    [Fact]
    public async Task Pending_and_rejected_rows_never_reach_the_cashbook_or_its_totals()
    {
        var (_, owner) = await SeedAsync();

        // An M-Pesa row the owner typed is a CLAIM: pending until somebody decides.
        var claim = await Finances().RecordIncomeAsync(new RecordIncomeRequest
        {
            Amount = 3000m,
            Source = "walk_in",
            PaymentMethod = "mpesa_till",
            TransactionReference = "QJK42NUM99",
            Description = "Claimed M-Pesa payment",
            TransactionDate = DateTime.UtcNow
        }, owner.Id, "Business");

        Assert.True(claim.Success);
        Assert.Equal("pending", claim.Data!.VerificationStatus);

        var pending = (await CashbookAsync(owner.Id)).Data!;
        Assert.Equal(0m, pending.TotalInflows);
        Assert.Empty(pending.Entries);

        var rejected = await Finances().RejectTransactionAsync(
            claim.Data.Id, new RejectFinancialTransactionRequest { Reason = "Not on our statement" },
            owner.Id, "Business");
        Assert.True(rejected.Success);

        var after = (await CashbookAsync(owner.Id)).Data!;
        Assert.Equal(0m, after.TotalInflows);
        Assert.Equal(0m, after.TotalOutflows);
        Assert.Empty(after.Entries);
    }
}
