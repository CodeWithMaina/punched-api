using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Application.Services;

/// <summary>The overview screen, and the totals the other surfaces reuse.</summary>
public partial class FinancialAnalyticsService
{
    public async Task<ApiResponse<FinancialOverviewResponse>> GetOverviewAsync(
        string? period, DateTime? from, DateTime? to, Guid userId, string role)
    {
        var businessId = await FinancialAccess.ResolveBusinessIdAsync(_context, userId);
        if (businessId is not Guid business)
            return ApiResponse<FinancialOverviewResponse>.Fail("FORBIDDEN", "No business scope for user.");

        var timeZone = await FinancialAccess.ResolveTimeZoneAsync(_context, business);
        var window = FinancialPeriod.Resolve(period, from, to, timeZone);

        var totals = await BuildTotalsAsync(business, window);
        var periodRows = PeriodQuery(business, window);

        var income = await periodRows
            .Where(t => t.Type == FinancialTransactionType.Income
                        && t.VerificationStatus == FinancialVerificationStatus.Verified
                        && !t.IsVoided)
            .Select(t => new IncomeSlice(t.Amount, t.Source, t.PaymentMethod))
            .ToListAsync();

        var expenses = await periodRows
            .Where(t => t.Type == FinancialTransactionType.Expense && !t.IsVoided)
            .Select(t => new ExpenseSlice(t.Amount, t.Category))
            .ToListAsync();

        var recent = await periodRows
            .OrderByDescending(t => t.TransactionDate)
            .ThenByDescending(t => t.Id)
            .Take(10)
            .ToListAsync();

        return ApiResponse<FinancialOverviewResponse>.Ok(new FinancialOverviewResponse
        {
            Period = window.Key,
            From = window.FromUtc,
            To = window.ToUtc,
            Totals = totals,
            RevenueBreakdown = Breakdown(income.Select(i => (i.Amount, i.Source)), DescribeSource),
            PaymentMethodBreakdown = Breakdown(income.Select(i => (i.Amount, i.Method)), DescribeMethod),
            ExpenseBreakdown = Breakdown(expenses.Select(e => (e.Amount, e.Category)), DescribeCategory),
            RecentActivity = recent.Select(r => FinancialMapper.ToResponse(r)).ToList()
        });
    }

    /// <summary>
    /// The six headline figures, each from its authoritative source:
    ///
    /// <list type="bullet">
    /// <item><b>verified income</b> — ledger income rows that are verified (pending and rejected excluded);</item>
    /// <item><b>pending verification</b> — ledger income rows recorded but not confirmed, PLUS payments still
    /// awaiting the customer or the owner (which correctly have no ledger row yet);</item>
    /// <item><b>expenses</b> / <b>refunds</b> — ledger outflow rows, non-voided (only completed refunds count as
    /// money out);</item>
    /// <item><b>net cash movement</b> — recorded verified inflows minus recorded outflows;</item>
    /// <item><b>outstanding balances</b> — what live appointments still owe, computed per appointment from its
    /// service total against settled payments — never by subtracting all-time income.</item>
    /// </list>
    /// </summary>
    private async Task<FinancialOverviewTotals> BuildTotalsAsync(Guid businessId, FinancialPeriod window)
    {
        // The window is bounded, and the projection is four narrow columns, so the totals are computed
        // in memory. That is deliberate: it keeps the figures identical on every database provider
        // (a provider that cannot SUM a decimal would otherwise silently change what these cards say)
        // and it reads the same rows the breakdowns above are built from.
        var rows = await PeriodQuery(businessId, window)
            .Select(t => new TotalRow(t.Amount, t.Type, t.VerificationStatus, t.IsVoided))
            .ToListAsync();

        var verifiedIncome = rows
            .Where(r => !r.IsVoided
                        && r.Type == FinancialTransactionType.Income
                        && r.VerificationStatus == FinancialVerificationStatus.Verified)
            .Sum(r => r.Amount);

        var pendingRows = rows
            .Where(r => !r.IsVoided
                        && r.Type == FinancialTransactionType.Income
                        && r.VerificationStatus == FinancialVerificationStatus.Pending)
            .ToList();

        var expenses = rows
            .Where(r => !r.IsVoided && r.Type == FinancialTransactionType.Expense)
            .Sum(r => r.Amount);

        var completedRefunds = rows
            .Where(r => !r.IsVoided
                        && r.Type == FinancialTransactionType.Refund
                        && r.VerificationStatus == FinancialVerificationStatus.Verified)
            .Sum(r => r.Amount);

        // Money the payment module is still waiting on. Read from the payments table on purpose: a
        // pending payment must never appear in the ledger, and omitting it entirely would hide the
        // business's real pipeline.
        var awaitingPaymentStatuses = new[]
        {
            PaymentStatus.Created,
            PaymentStatus.AwaitingCustomer,
            PaymentStatus.Submitted,
            PaymentStatus.UnderReview
        };

        var awaitingAmounts = await _context.Payments.AsNoTracking()
            .Where(p => p.BusinessId == businessId
                        && awaitingPaymentStatuses.Contains(p.Status)
                        && p.CreatedAt >= window.FromUtc
                        && p.CreatedAt < window.ToUtc)
            .Select(p => p.Amount)
            .ToListAsync();

        var paymentsAwaiting = awaitingAmounts.Sum();

        var outstanding = await OutstandingBalancesAsync(businessId);

        return new FinancialOverviewTotals
        {
            VerifiedIncome = verifiedIncome,
            PendingVerification = pendingRows.Sum(r => r.Amount) + paymentsAwaiting,
            PendingVerificationCount = pendingRows.Count,
            PaymentsAwaitingVerification = paymentsAwaiting,
            TotalExpenses = expenses,
            TotalRefunds = completedRefunds,
            NetCashMovement = verifiedIncome - expenses - completedRefunds,
            OutstandingBalances = outstanding.Total,
            OutstandingAppointmentCount = outstanding.Count,
            Currency = "KES"
        };
    }

    private readonly record struct TotalRow(
        decimal Amount, FinancialTransactionType Type, FinancialVerificationStatus VerificationStatus, bool IsVoided);

    /// <summary>
    /// What customers still owe, appointment by appointment.
    ///
    /// <para><b>Why not "appointment values minus all income".</b> That mixes business-wide income
    /// with per-appointment obligations and reads as nonsense the moment a business takes walk-in
    /// money. Instead each live appointment's service total is compared with the payments settled
    /// against IT, and completed refunds reduce what it is treated as having paid — so a refunded
    /// visit correctly shows a balance again.</para>
    /// </summary>
    private async Task<(decimal Total, int Count)> OutstandingBalancesAsync(Guid businessId)
    {
        // Live appointment ids, then their service-line prices. Two narrow queries rather than a
        // correlated SUM, so the obligation figure is computed in memory and means the same thing on
        // every provider.
        var liveAppointments = await _context.Appointments.AsNoTracking()
            .Where(a => a.BusinessId == businessId && a.Status != "cancelled")
            .Select(a => a.Id)
            .ToListAsync();

        if (liveAppointments.Count == 0) return (0m, 0);

        var obligations = await _context.AppointmentResources.AsNoTracking()
            .Where(r => liveAppointments.Contains(r.AppointmentId))
            .Select(r => new AppointmentPriceRow(r.AppointmentId, r.Price))
            .ToListAsync();

        var obligationByAppointment = obligations
            .GroupBy(o => o.AppointmentId)
            .ToDictionary(g => g.Key, g => g.Sum(o => o.Price));

        if (obligationByAppointment.Count == 0) return (0m, 0);

        var settledStates = new[] { PaymentStatus.Success, PaymentStatus.Verified };

        var paidRows = await _context.Payments.AsNoTracking()
            .Where(p => p.BusinessId == businessId
                        && p.AppointmentId != null
                        && settledStates.Contains(p.Status))
            .Select(p => new AppointmentAmountRow(p.AppointmentId!.Value, p.Amount))
            .ToListAsync();

        var paid = paidRows
            .GroupBy(p => p.AppointmentId)
            .ToDictionary(g => g.Key, g => g.Sum(p => p.Amount));

        // Completed refunds reduce what an appointment is treated as having paid, so a refunded visit
        // correctly shows a balance again.
        var refundRows = await _context.FinancialTransactions.AsNoTracking()
            .Where(t => t.BusinessId == businessId
                        && t.Type == FinancialTransactionType.Refund
                        && t.VerificationStatus == FinancialVerificationStatus.Verified
                        && !t.IsVoided
                        && t.AppointmentId != null)
            .Select(t => new AppointmentAmountRow(t.AppointmentId!.Value, t.Amount))
            .ToListAsync();

        var refunded = refundRows
            .GroupBy(t => t.AppointmentId)
            .ToDictionary(g => g.Key, g => g.Sum(t => t.Amount));

        var total = 0m;
        var count = 0;

        foreach (var (appointmentId, obligation) in obligationByAppointment)
        {
            var netPaid = paid.GetValueOrDefault(appointmentId) - refunded.GetValueOrDefault(appointmentId);
            var outstanding = obligation - netPaid;

            if (outstanding <= 0m) continue;

            total += outstanding;
            count++;
        }

        return (total, count);
    }
}
