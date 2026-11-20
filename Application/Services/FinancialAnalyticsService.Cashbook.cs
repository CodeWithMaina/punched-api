using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Application.Services;

/// <summary>The cashbook: one chronological view of everything the business recorded moving.</summary>
public partial class FinancialAnalyticsService
{
    /// <summary>
    /// Every recorded movement for a period, newest first, with the net.
    ///
    /// <para><b>Cashbook vs payment verification.</b> This screen shows the <i>ledger</i> — money the
    /// business recorded as earned, spent or refunded. It deliberately does not list the payment
    /// verification queue: a pasted M-Pesa message waiting for a decision is not yet a movement, and
    /// mixing the two would suggest money had arrived when it had not.</para>
    /// </summary>
    public async Task<ApiResponse<CashbookResponse>> GetCashbookAsync(
        FinancialTransactionQuery query, Guid userId, string role)
    {
        var businessId = await FinancialAccess.ResolveBusinessIdAsync(_context, userId);
        if (businessId is not Guid business)
            return ApiResponse<CashbookResponse>.Fail("FORBIDDEN", "No business scope for user.");

        var timeZone = await FinancialAccess.ResolveTimeZoneAsync(_context, business);

        // The cashbook always works on a resolved window: either the caller's custom range or the
        // default 30 days. A cashbook with no window has no meaningful net.
        var window = query.From.HasValue || query.To.HasValue
            ? FinancialPeriod.Resolve(
                FinancialPeriod.Custom,
                LocalDate(query.From, timeZone),
                LocalDate(query.To, timeZone),
                timeZone)
            : FinancialPeriod.Resolve("last_30_days", null, null, timeZone);

        query.From = window.FromUtc;
        query.To = window.ToUtc.AddTicks(-1);

        var filtered = FinancialQueries.ApplyFilters(
            _context, _context.FinancialTransactions.AsNoTracking(), query, business);

        // The Cashbook is the master financial book, so it contains MOVEMENTS only. A row that is
        // still pending is a claim somebody made, and a rejected one never happened — neither is
        // money the business received or spent, and neither may appear in the book or in its
        // totals. (They are not lost: claims live in Reconciliation, and an owner-recorded claim
        // is listed in Transactions.) This is why the register and the totals are read from the
        // same filtered set below: they cannot disagree about what counts.
        var recognised = filtered.Where(t => t.VerificationStatus == FinancialVerificationStatus.Verified);

        var (page, pageSize, skip) = FinancialQueries.Paging(query.Page, query.PageSize);

        var total = await recognised.CountAsync();

        // Two scopes, deliberately kept apart (the reference design shows both):
        //
        //  - the VIEW totals (`inflows`… below) describe the rows the reader has filtered to, so
        //    "net in this view" is true even while a category or search is applied;
        //  - the WINDOW figures (running balance, entry count, method breakdown, `window*`) describe
        //    the whole verified book for the period and never move when a filter changes.
        //
        // A running balance that started at the first row of a search result would be a different
        // number on every keystroke — and none of them the book's.
        var windowRows = await _context.FinancialTransactions.AsNoTracking()
            .Where(t => t.BusinessId == business
                        && t.VerificationStatus == FinancialVerificationStatus.Verified
                        && t.TransactionDate >= window.FromUtc
                        && t.TransactionDate < window.ToUtc)
            .Select(t => new LedgerMovementRow(
                t.Id, t.TransactionDate, t.Amount, t.Type, t.PaymentMethod,
                t.VerificationStatus, t.IsVoided, t.CreatedByUserId))
            .ToListAsync();

        // SQLite cannot SUM a decimal, and this service must answer identically on every provider,
        // so the filtered figures are summed in memory from one narrow projection — exactly as the
        // window totals are.
        var viewMovements = await recognised
            .Select(t => new { t.Amount, t.Type, t.IsVoided })
            .ToListAsync();

        var inflows = viewMovements
            .Where(t => !t.IsVoided && t.Type == FinancialTransactionType.Income)
            .Sum(t => t.Amount);

        var expenses = viewMovements
            .Where(t => !t.IsVoided && t.Type == FinancialTransactionType.Expense)
            .Sum(t => t.Amount);

        var refunds = viewMovements
            .Where(t => !t.IsVoided && t.Type == FinancialTransactionType.Refund)
            .Sum(t => t.Amount);

        var windowInflows = windowRows
            .Where(r => !r.IsVoided && r.Type == FinancialTransactionType.Income)
            .Sum(r => r.Amount);

        var windowOutflows = windowRows
            .Where(r => !r.IsVoided && r.Type is FinancialTransactionType.Expense or FinancialTransactionType.Refund)
            .Sum(r => r.Amount);

        // The running balance, oldest first, starting at zero on the first day of the window.
        // Punched has never seen an opening balance, so none is invented: this is the running NET
        // of what was recorded inside the window, not the balance of any account. Voided rows moved
        // nothing, so they leave the cumulative figure untouched.
        var runningBalances = new Dictionary<Guid, decimal>(windowRows.Count);
        var cumulative = 0m;

        foreach (var row in windowRows
            .OrderBy(r => r.TransactionDate)
            .ThenBy(r => r.Id))
        {
            if (!row.IsVoided)
            {
                cumulative += row.Type is FinancialTransactionType.Income or FinancialTransactionType.Adjustment
                    ? row.Amount
                    : -row.Amount;
            }

            runningBalances[row.Id] = cumulative;
        }

        // Verified income by method, over the same window and the same rule as `windowInflows` — so
        // the bars can never add up to a different number than the legend above them.
        var methodBreakdown = windowRows
            .Where(r => !r.IsVoided && r.Type == FinancialTransactionType.Income)
            .GroupBy(r => FinancialWire.MethodGroup(r.PaymentMethod))
            .Select(group => new FinancialBreakdownItem
            {
                Key = group.Key.Key,
                Label = group.Key.Label,
                Amount = group.Sum(r => r.Amount),
                Count = group.Count(),
                Percentage = windowInflows <= 0
                    ? 0
                    : Math.Round(group.Sum(r => r.Amount) / windowInflows * 100m, 1, MidpointRounding.AwayFromZero)
            })
            .OrderByDescending(item => item.Amount)
            .ToList();

        var rows = await FinancialQueries.ApplySort(recognised, query.Sort, query.SortDirection)
            .Skip(skip)
            .Take(pageSize)
            .ToListAsync();

        // Names for the visible page only, in one query.
        var names = await FinancialAccess.ResolveUserNamesAsync(_context, rows.Select(r => r.CreatedByUserId));

        return ApiResponse<CashbookResponse>.Ok(new CashbookResponse
        {
            Period = window.Key,
            From = window.FromUtc,
            To = window.ToUtc,
            Entries = rows.Select(t => ToCashbookEntry(
                t,
                runningBalances.GetValueOrDefault(t.Id),
                t.CreatedByUserId is Guid recorder ? names.GetValueOrDefault(recorder) : null)).ToList(),
            Page = page,
            PageSize = pageSize,
            TotalCount = total,
            TotalInflows = inflows,
            TotalOutflows = expenses + refunds,
            Expenses = expenses,
            Refunds = refunds,
            NetCashMovement = inflows - expenses - refunds,
            EntryCount = windowRows.Count(r => !r.IsVoided),
            WindowInflows = windowInflows,
            WindowOutflows = windowOutflows,
            WindowNetCashMovement = windowInflows - windowOutflows,
            PaymentMethodBreakdown = methodBreakdown,
            TotalPages = pageSize <= 0 ? 1 : (int)Math.Ceiling(total / (double)pageSize),
            Currency = "KES"
        });
    }

    /// <summary>
    /// What one ledger row looks like as a movement: which way the money went, what it was for,
    /// who recorded it and where the running net stood when it was recorded.
    /// Income and adjustments are money in; expenses and refunds are money out.
    /// </summary>
    private static CashbookEntryResponse ToCashbookEntry(
        FinancialTransaction t, decimal runningBalance, string? recordedByName)
    {
        return new CashbookEntryResponse
        {
            Id = t.Id,
            Date = t.TransactionDate,
            Type = FinancialWire.ToWire(t.Type),
            // The direction is the server's own classification, never inferred from the type name on
            // the client: a refund is money out even though it reverses money in.
            Direction = FinancialWire.ToWire(FinancialWire.DirectionOf(t.Type)),
            Amount = t.Amount,
            Description = t.Description,
            Reference = t.Reference,
            ExternalReference = t.ExternalReference,
            PaymentMethod = FinancialWire.ToWire(t.PaymentMethod),
            Source = FinancialWire.ToWire(t.Source),
            CustomerName = t.CustomerName,
            SourcePaymentId = t.SourcePaymentId,
            Category = t.Category,
            VerificationStatus = FinancialWire.ToWire(t.VerificationStatus),
            IsVoided = t.IsVoided,
            RunningBalance = runningBalance,
            RecordedByName = recordedByName
        };
    }

    /// <summary>
    /// The local calendar date of a client-supplied instant. The client sends an ISO instant for the
    /// range it picked, and the window must be expressed in the business's days — not the server's.
    /// </summary>
    private static DateTime? LocalDate(DateTime? instant, TimeZoneInfo timeZone)
    {
        if (instant is not DateTime value) return null;

        var utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
        return TimeZoneInfo.ConvertTimeFromUtc(utc, timeZone);
    }
}
