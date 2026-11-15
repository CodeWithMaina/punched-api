using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

/// <summary>
/// The financial overview, the cashbook and the analytics.
///
/// <para><b>One definition per figure.</b> Every total is computed by the same private helpers, so
/// "Total expenses" on the overview, in the cashbook and in the analytics is the same number — and
/// none of them is derived by subtracting one module's rows from another's. The ledger is the
/// source for money movement; the payments module is consulted ONLY for money that is still
/// pending, because a pending payment correctly has no ledger row.</para>
///
/// <para><b>What these numbers are not.</b> Net cash movement is what the business RECORDED, not
/// the balance of a bank or M-Pesa account Punched has never seen.</para>
/// </summary>
public partial class FinancialAnalyticsService : IFinancialAnalyticsService
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<FinancialAnalyticsService> _logger;

    public FinancialAnalyticsService(ApplicationDbContext context, ILogger<FinancialAnalyticsService> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>Every ledger row for the window, tenant-scoped.</summary>
    private IQueryable<FinancialTransaction> PeriodQuery(Guid businessId, FinancialPeriod window) =>
        _context.FinancialTransactions.AsNoTracking()
            .Where(t => t.BusinessId == businessId
                        && t.TransactionDate >= window.FromUtc
                        && t.TransactionDate < window.ToUtc);

    private readonly record struct IncomeSlice(decimal Amount, TransactionSource Source, FinancialPaymentMethod Method);

    private readonly record struct ExpenseSlice(decimal Amount, string? Category);

    /// <summary>
    /// Groups amounts by a key and labels each slice, ordered by size (the biggest contributor is
    /// the answer the owner is looking for). Percentages are computed against the breakdown's own
    /// total, so the slices always add to 100%.
    /// </summary>
    private static List<FinancialBreakdownItem> Breakdown<TKey>(
        IEnumerable<(decimal Amount, TKey Key)> rows,
        Func<TKey, (string Key, string Label)> describe)
    {
        var groups = new Dictionary<string, (string Label, decimal Amount, int Count)>(StringComparer.Ordinal);

        foreach (var (amount, key) in rows)
        {
            var (groupKey, label) = describe(key);

            var current = groups.TryGetValue(groupKey, out var found)
                ? found
                : (Label: label, Amount: 0m, Count: 0);
            groups[groupKey] = (current.Label, current.Amount + amount, current.Count + 1);
        }

        var total = groups.Values.Sum(g => g.Amount);

        return groups
            .Select(pair => new FinancialBreakdownItem
            {
                Key = pair.Key,
                Label = pair.Value.Label,
                Amount = pair.Value.Amount,
                Count = pair.Value.Count,
                Percentage = total > 0m
                    ? decimal.Round(pair.Value.Amount * 100m / total, 1, MidpointRounding.AwayFromZero)
                    : 0m
            })
            .OrderByDescending(item => item.Amount)
            .ToList();
    }

    private static (string Key, string Label) DescribeSource(TransactionSource source) =>
        (FinancialWire.ToWire(source), LabelFor(source));

    private static (string Key, string Label) DescribeMethod(FinancialPaymentMethod method) =>
        (FinancialWire.ToWire(method), LabelFor(method));

    private static (string Key, string Label) DescribeCategory(string? category) =>
        category == null ? ("uncategorised", "Uncategorised") : (category, ExpenseCategories.Label(category));

    private static string LabelFor(TransactionSource source) => source switch
    {
        TransactionSource.PunchedBooking => "Punched appointments",
        TransactionSource.WalkIn => "Walk-ins",
        TransactionSource.ProductSale => "Product sales",
        TransactionSource.ManualIncome => "Manually recorded",
        _ => "Other income"
    };

    private static string LabelFor(FinancialPaymentMethod method) => method switch
    {
        FinancialPaymentMethod.MpesaTill => "M-Pesa Till",
        FinancialPaymentMethod.MpesaPaybill => "M-Pesa Paybill",
        FinancialPaymentMethod.MpesaSendMoney => "M-Pesa Send Money",
        FinancialPaymentMethod.Cash => "Cash",
        FinancialPaymentMethod.BankTransfer => "Bank transfer",
        FinancialPaymentMethod.Card => "Card",
        _ => "Other"
    };
}
