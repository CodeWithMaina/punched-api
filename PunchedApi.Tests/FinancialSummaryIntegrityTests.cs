using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;
using PunchedApi.Infrastructure.Repositories;

namespace PunchedApi.Tests;

/// <summary>
/// The financial summary, the consolidated cashbook and the refund path.
///
/// <para><b>What these pin.</b> The summary used to fail with
/// <c>Could not load type '&lt;&gt;f__AnonymousType63`2' from assembly 'PunchedApi'</c> — an EF
/// Core query plan cached against a compiler-generated projection type that a rebuild had
/// renumbered. The projections are now named records, and the first two tests below are the
/// regression guard: the summary must load, and every response must serialise cleanly with the
/// direction stated by the server rather than inferred by the client.</para>
/// </summary>
public class FinancialSummaryIntegrityTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public FinancialSummaryIntegrityTests()
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
        _context,
        new UnitOfWork(_context),
        TestHelpers.CreateLogger<FinancialService>(),
        new IdempotencyService(new UnitOfWork(_context), _context, TestHelpers.CreateLogger<IdempotencyService>()));

    private ExpenseService Expenses() => new(
        _context, new UnitOfWork(_context), TestHelpers.CreateLogger<ExpenseService>());

    private FinancialAnalyticsService Analytics() => new(
        _context, TestHelpers.CreateLogger<FinancialAnalyticsService>());

    private async Task<(Business business, User owner)> SeedAsync()
    {
        var owner = BookingTestBase.CreateOwner("summary@test.com");
        var business = BookingTestBase.CreateBusiness(owner.Id, "Summary Salon");
        await BookingTestBase.SeedAsync(_context, owner, business);
        return (business, owner);
    }

    private Task<ApiResponse<FinancialTransactionResponse>> IncomeAsync(
        Guid ownerId, decimal amount, string method = "cash", string source = "walk_in", string? reference = null) =>
        Finances().RecordIncomeAsync(new RecordIncomeRequest
        {
            Amount = amount,
            Source = source,
            PaymentMethod = method,
            TransactionReference = reference,
            Description = "Recorded income",
            TransactionDate = DateTime.UtcNow
        }, ownerId, "Business");

    private Task<ApiResponse<FinancialTransactionResponse>> ExpenseAsync(
        Guid ownerId, decimal amount, string description = "Stock") =>
        Expenses().CreateExpenseAsync(new CreateExpenseRequest
        {
            Amount = amount,
            Category = ExpenseCategories.StockAndSupplies,
            PaymentMethod = "cash",
            Description = description,
            TransactionDate = DateTime.UtcNow
        }, ownerId, "Business");

    private Task<ApiResponse<CashbookResponse>> CashbookAsync(
        Guid ownerId, FinancialTransactionQuery? query = null) =>
        Analytics().GetCashbookAsync(query ?? new FinancialTransactionQuery { PageSize = 50 }, ownerId, "Business");

    /// <summary>One verified receipt, one expense, one completed refund, one pending and one rejected claim.</summary>
    private async Task SeedMixedBookAsync(Guid ownerId)
    {
        Assert.True((await IncomeAsync(ownerId, 5000m)).Success);

        var productSale = await IncomeAsync(ownerId, 2000m, source: "product_sale");
        Assert.True(productSale.Success);

        Assert.True((await ExpenseAsync(ownerId, 1500m, "Shop rent")).Success);

        var refund = await Finances().RecordRefundAsync(new RecordRefundRequest
        {
            TransactionId = productSale.Data!.Id,
            Amount = 500m,
            Reason = "Customer changed their mind",
            RefundMethod = "cash",
            Completed = true
        }, ownerId, "Business");
        Assert.True(refund.Success);

        // A pending claim: money a customer says they sent, which nobody has decided yet.
        var claim = await IncomeAsync(ownerId, 3000m, method: "mpesa_till", reference: "QJK42NUM77");
        Assert.True(claim.Success);
        Assert.Equal("pending", claim.Data!.VerificationStatus);

        // A rejected claim: never income, never in the book.
        var rejected = await IncomeAsync(ownerId, 1000m, method: "mpesa_till", reference: "QJK42NUM78");
        Assert.True(rejected.Success);
        Assert.True((await Finances().RejectTransactionAsync(
            rejected.Data!.Id, new RejectFinancialTransactionRequest { Reason = "Not on our statement" },
            ownerId, "Business")).Success);
    }

    [Fact]
    public async Task The_financial_summary_loads_with_a_mixed_book_and_counts_every_figure_once()
    {
        var (_, owner) = await SeedAsync();
        await SeedMixedBookAsync(owner.Id);

        var overview = await Analytics().GetOverviewAsync("last_30_days", null, null, owner.Id, "Business");
        Assert.True(overview.Success);

        var totals = overview.Data!.Totals;
        // 5,000 + 2,000 recognised; the 500 refund and the 1,500 expense are out; the pending and
        // rejected claims are neither.
        Assert.Equal(7000m, totals.VerifiedIncome);
        Assert.Equal(1500m, totals.TotalExpenses);
        Assert.Equal(500m, totals.TotalRefunds);
        Assert.Equal(5000m, totals.NetCashMovement);
        // The pending claim is reported as pending — never folded into verified income.
        Assert.Equal(3000m, totals.PendingVerification);
    }

    [Fact]
    public async Task The_summary_and_the_cashbook_serialise_with_the_direction_stated_by_the_server()
    {
        var (_, owner) = await SeedAsync();
        await SeedMixedBookAsync(owner.Id);

        var overview = await Analytics().GetOverviewAsync("last_30_days", null, null, owner.Id, "Business");
        var cashbook = await CashbookAsync(owner.Id);

        // Serialisation is what failed before: an anonymous projection type that a rebuild had
        // renumbered could not be loaded while the response was being written.
        var overviewJson = JsonSerializer.Serialize(overview.Data);
        var cashbookJson = JsonSerializer.Serialize(cashbook.Data);

        Assert.Contains("verifiedIncome", overviewJson);
        Assert.Contains("\"direction\":\"incoming\"", cashbookJson);
        Assert.Contains("\"direction\":\"outgoing\"", cashbookJson);

        // And it round-trips, so the contract is a stable, serialisable shape.
        var replayed = JsonSerializer.Deserialize<CashbookResponse>(cashbookJson);
        Assert.NotNull(replayed);
        Assert.Equal(cashbook.Data!.NetCashMovement, replayed!.NetCashMovement);
    }

    [Fact]
    public async Task Direction_partitions_the_cashbook_and_the_totals_follow_the_filter()
    {
        var (_, owner) = await SeedAsync();
        await SeedMixedBookAsync(owner.Id);

        var moneyIn = await CashbookAsync(owner.Id, new FinancialTransactionQuery { Direction = "money_in", PageSize = 50 });
        Assert.True(moneyIn.Success);
        Assert.All(moneyIn.Data!.Entries, entry => Assert.Equal("incoming", entry.Direction));
        Assert.Equal(7000m, moneyIn.Data.TotalInflows);
        Assert.Equal(0m, moneyIn.Data.TotalOutflows);

        var moneyOut = await CashbookAsync(owner.Id, new FinancialTransactionQuery { Direction = "out", PageSize = 50 });
        Assert.True(moneyOut.Success);
        Assert.All(moneyOut.Data!.Entries, entry => Assert.Equal("outgoing", entry.Direction));
        // An expense and a refund: 1,500 + 500.
        Assert.Equal(2000m, moneyOut.Data.TotalOutflows);
        Assert.Equal(0m, moneyOut.Data.TotalInflows);
        // A refund is money OUT even though it reverses money in.
        Assert.Contains(moneyOut.Data.Entries, entry => entry.Type == "refund");
    }

    [Fact]
    public async Task The_cashbook_paginates_the_filtered_set_without_repeating_or_losing_a_row()
    {
        var (_, owner) = await SeedAsync();

        for (var index = 0; index < 5; index++)
            Assert.True((await ExpenseAsync(owner.Id, 100m + index, $"Expense {index}")).Success);

        var first = await CashbookAsync(owner.Id, new FinancialTransactionQuery { PageSize = 2, Page = 1 });
        var second = await CashbookAsync(owner.Id, new FinancialTransactionQuery { PageSize = 2, Page = 2 });
        var third = await CashbookAsync(owner.Id, new FinancialTransactionQuery { PageSize = 2, Page = 3 });

        Assert.Equal(5, first.Data!.TotalCount);
        Assert.Equal(3, first.Data.TotalPages);
        Assert.Equal(2, first.Data.Entries.Count);
        Assert.Equal(2, second.Data!.Entries.Count);
        Assert.Single(third.Data!.Entries);

        var ids = first.Data.Entries
            .Concat(second.Data.Entries)
            .Concat(third.Data.Entries)
            .Select(entry => entry.Id)
            .ToList();

        // Stable ordering is what stops a row appearing on two pages while another disappears.
        Assert.Equal(5, ids.Distinct().Count());
    }

    [Fact]
    public async Task A_repeated_refund_with_the_same_idempotency_key_is_recorded_exactly_once()
    {
        var (_, owner) = await SeedAsync();

        var income = await IncomeAsync(owner.Id, 2000m);
        Assert.True(income.Success);

        var request = new RecordRefundRequest
        {
            TransactionId = income.Data!.Id,
            Amount = 500m,
            Reason = "Partial goodwill refund",
            RefundMethod = "cash",
            Completed = true,
            IdempotencyKey = "refund-attempt-1"
        };

        var first = await Finances().RecordRefundAsync(request, owner.Id, "Business");
        Assert.True(first.Success);

        // The retry: same key, same body. It must return the FIRST refund, not record a second.
        var second = await Finances().RecordRefundAsync(request, owner.Id, "Business");
        Assert.True(second.Success);
        Assert.Equal(first.Data!.Id, second.Data!.Id);

        var refundRows = await _context.FinancialTransactions.AsNoTracking()
            .Where(t => t.Type == FinancialTransactionType.Refund
                        && t.RefundOfTransactionId == income.Data.Id)
            .ToListAsync();

        Assert.Single(refundRows);
        Assert.Equal(500m, refundRows[0].Amount);

        // The original receipt is untouched and still refundable down to what is left.
        var original = await Finances().GetTransactionAsync(income.Data.Id, owner.Id, "Business");
        Assert.True(original.Success);
        Assert.Equal(2000m, original.Data!.Amount);
        Assert.Equal(1500m, original.Data.RefundableAmount);
    }

    [Fact]
    public async Task A_refund_beyond_what_is_left_is_refused_without_touching_the_original()
    {
        var (_, owner) = await SeedAsync();

        var income = await IncomeAsync(owner.Id, 2000m);
        Assert.True(income.Success);

        Assert.True((await Finances().RecordRefundAsync(new RecordRefundRequest
        {
            TransactionId = income.Data!.Id,
            Amount = 1500m,
            Reason = "First refund",
            RefundMethod = "cash",
            Completed = true
        }, owner.Id, "Business")).Success);

        // 500 is left; 600 is not.
        var tooMuch = await Finances().RecordRefundAsync(new RecordRefundRequest
        {
            TransactionId = income.Data.Id,
            Amount = 600m,
            Reason = "Second refund",
            RefundMethod = "cash",
            Completed = true
        }, owner.Id, "Business");

        Assert.False(tooMuch.Success);
        Assert.Equal("REFUND_EXCEEDS_AVAILABLE", tooMuch.Error!.Code);

        var original = await Finances().GetTransactionAsync(income.Data.Id, owner.Id, "Business");
        Assert.Equal(2000m, original.Data!.Amount);
        Assert.Equal(500m, original.Data.RefundableAmount);

        var refundCount = await _context.FinancialTransactions.AsNoTracking()
            .CountAsync(t => t.Type == FinancialTransactionType.Refund
                             && t.RefundOfTransactionId == income.Data.Id);
        Assert.Equal(1, refundCount);
    }
}
