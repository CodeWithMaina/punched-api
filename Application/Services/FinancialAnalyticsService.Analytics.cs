using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Application.Services;

/// <summary>
/// Financial analytics: revenue, payments, expenses and the period's operating result — all from the
/// ledger, so nothing here can disagree with the overview.
/// </summary>
public partial class FinancialAnalyticsService
{
    public async Task<ApiResponse<FinancialAnalyticsResponse>> GetAnalyticsAsync(
        string? period, DateTime? from, DateTime? to, Guid userId, string role)
    {
        var businessId = await FinancialAccess.ResolveBusinessIdAsync(_context, userId);
        if (businessId is not Guid business)
            return ApiResponse<FinancialAnalyticsResponse>.Fail("FORBIDDEN", "No business scope for user.");

        var timeZone = await FinancialAccess.ResolveTimeZoneAsync(_context, business);
        var window = FinancialPeriod.Resolve(period, from, to, timeZone);

        var response = new FinancialAnalyticsResponse
        {
            Period = window.Key,
            From = window.FromUtc,
            To = window.ToUtc,
            Currency = "KES"
        };

        await FillRevenueAsync(response, business, window);
        await FillPaymentsAsync(response, business, window);
        await FillExpensesAsync(response, business, window);
        await FillPerformanceAsync(response, business, window);

        return ApiResponse<FinancialAnalyticsResponse>.Ok(response);
    }

    /// <summary>Verified income: its trend, its sources and methods, and who/what earned it.</summary>
    private async Task FillRevenueAsync(FinancialAnalyticsResponse response, Guid businessId, FinancialPeriod window)
    {
        var incomeRows = await PeriodQuery(businessId, window)
            .Where(t => t.Type == FinancialTransactionType.Income
                        && t.VerificationStatus == FinancialVerificationStatus.Verified
                        && !t.IsVoided)
            .Select(t => new RevenueRow(t.Amount, t.Source, t.PaymentMethod, t.AppointmentId, t.TransactionDate))
            .ToListAsync();

        var revenue = response.Revenue;
        revenue.Granularity = window.Granularity;
        revenue.OverTime = Trend(incomeRows.Select(r => (r.Date, r.Amount)), window.Granularity);
        revenue.BySource = Breakdown(incomeRows.Select(r => (r.Amount, r.Source)), DescribeSource);
        revenue.ByPaymentMethod = Breakdown(incomeRows.Select(r => (r.Amount, r.Method)), DescribeMethod);
        revenue.AverageTransactionValue = incomeRows.Count > 0
            ? decimal.Round(incomeRows.Sum(r => r.Amount) / incomeRows.Count, 2, MidpointRounding.AwayFromZero)
            : 0m;

        var attributed = await AttributeToServicesAsync(businessId, incomeRows);
        revenue.ByService = attributed.ByService;
        revenue.ByCategory = attributed.ByCategory;
        revenue.ByStaff = attributed.ByStaff;
    }

    private readonly record struct RevenueRow(
        decimal Amount, TransactionSource Source, FinancialPaymentMethod Method, Guid? AppointmentId, DateTime Date);

    /// <summary>
    /// Verifies the money has actually been collected AND separates the states the owner must
    /// distinguish: verified money, money still waiting on a decision, and money that was refused.
    /// </summary>
    private async Task FillPaymentsAsync(FinancialAnalyticsResponse response, Guid businessId, FinancialPeriod window)
    {
        var rows = await PeriodQuery(businessId, window)
            .Where(t => t.Type == FinancialTransactionType.Income && !t.IsVoided)
            .Select(t => new IncomeBreakdownRow(t.Amount, t.PaymentMethod, t.VerificationStatus, t.TransactionDate))
            .ToListAsync();

        var payments = response.Payments;

        payments.ByMethod = Breakdown(
            rows.Where(r => r.VerificationStatus == FinancialVerificationStatus.Verified)
                .Select(r => (r.Amount, r.PaymentMethod)),
            DescribeMethod);

        payments.VerifiedAmount = rows.Where(r => r.VerificationStatus == FinancialVerificationStatus.Verified)
            .Sum(r => r.Amount);
        payments.VerifiedCount = rows.Count(r => r.VerificationStatus == FinancialVerificationStatus.Verified);

        // Pending rows are reported as pending and never folded into verified money.
        payments.PendingAmount = rows.Where(r => r.VerificationStatus == FinancialVerificationStatus.Pending)
            .Sum(r => r.Amount);
        payments.PendingCount = rows.Count(r => r.VerificationStatus == FinancialVerificationStatus.Pending);

        payments.RejectedAmount = rows.Where(r => r.VerificationStatus == FinancialVerificationStatus.Rejected)
            .Sum(r => r.Amount);
        payments.RejectedCount = rows.Count(r => r.VerificationStatus == FinancialVerificationStatus.Rejected);

        var refunds = await PeriodQuery(businessId, window)
            .Where(t => t.Type == FinancialTransactionType.Refund && !t.IsVoided)
            .Select(t => new TrendRow(t.Amount, t.TransactionDate, t.VerificationStatus))
            .ToListAsync();

        payments.RefundsOverTime = Trend(refunds.Select(r => (r.TransactionDate, r.Amount)), window.Granularity);
        payments.RefundTotal = refunds
            .Where(r => r.VerificationStatus == FinancialVerificationStatus.Verified)
            .Sum(r => r.Amount);
        payments.RefundCount = refunds.Count;
    }

    /// <summary>Where the money went: the trend and the categories (voided rows excluded everywhere).</summary>
    private async Task FillExpensesAsync(FinancialAnalyticsResponse response, Guid businessId, FinancialPeriod window)
    {
        var rows = await PeriodQuery(businessId, window)
            .Where(t => t.Type == FinancialTransactionType.Expense && !t.IsVoided)
            .Select(t => new ExpenseTrendRow(t.Amount, t.Category, t.TransactionDate))
            .ToListAsync();

        var expenses = response.Expenses;
        expenses.Total = rows.Sum(r => r.Amount);
        expenses.Count = rows.Count;
        expenses.OverTime = Trend(rows.Select(r => (r.TransactionDate, r.Amount)), window.Granularity);
        expenses.ByCategory = Breakdown(rows.Select(r => (r.Amount, r.Category)), DescribeCategory);
    }

    /// <summary>The period in the plainest terms: income, outgoings, what is still owed, and the rate.</summary>
    private async Task FillPerformanceAsync(
        FinancialAnalyticsResponse response, Guid businessId, FinancialPeriod window)
    {
        var totals = await BuildTotalsAsync(businessId, window);

        var performance = response.Performance;
        performance.TotalIncome = totals.VerifiedIncome;
        performance.TotalExpenses = totals.TotalExpenses;
        performance.TotalRefunds = totals.TotalRefunds;
        performance.NetOperatingResult = totals.VerifiedIncome - totals.TotalExpenses - totals.TotalRefunds;
        performance.OutstandingBalances = totals.OutstandingBalances;
        performance.OutstandingAppointmentCount = totals.OutstandingAppointmentCount;
        performance.AverageTransactionValue = response.Revenue.AverageTransactionValue;

        performance.RefundRate = totals.VerifiedIncome > 0m
            ? decimal.Round(totals.TotalRefunds * 100m / totals.VerifiedIncome, 1, MidpointRounding.AwayFromZero)
            : 0m;

        performance.MarginPercentage = totals.VerifiedIncome > 0m
            ? decimal.Round(
                (totals.VerifiedIncome - totals.TotalExpenses - totals.TotalRefunds) * 100m / totals.VerifiedIncome,
                1,
                MidpointRounding.AwayFromZero)
            : 0m;
    }
}
