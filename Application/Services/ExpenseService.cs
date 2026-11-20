using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

/// <summary>
/// Expenses: the money the business spends.
///
/// <para><b>Why editing is allowed at all.</b> Owners mistype amounts and mis-date receipts, and
/// a book that cannot be corrected is a book people stop using. So an expense may be edited —
/// but only through an audited path that records the before and after, and only while it is not
/// already reconciled. A reconciled expense is settled history.</para>
///
/// <para><b>Why voiding replaces deleting.</b> Deleting would remove the evidence that the money
/// was ever recorded — the one thing an audit looks for. Voiding keeps the row visible, drops it
/// from every total, and demands a reason.</para>
/// </summary>
public class ExpenseService : IExpenseService
{
    private readonly ApplicationDbContext _context;
    private readonly IUnitOfWork _uow;
    private readonly ILogger<ExpenseService> _logger;

    public ExpenseService(
        ApplicationDbContext context,
        IUnitOfWork uow,
        ILogger<ExpenseService> logger)
    {
        _context = context;
        _uow = uow;
        _logger = logger;
    }

    /// <summary>The predefined categories, in the order the UI should offer them.</summary>
    public ApiResponse<List<ExpenseCategoryResponse>> GetCategories() =>
        ApiResponse<List<ExpenseCategoryResponse>>.Ok(
            ExpenseCategories.All
                .Select(key => new ExpenseCategoryResponse { Key = key, Label = ExpenseCategories.Label(key) })
                .ToList());

    public async Task<ApiResponse<FinancialTransactionResponse>> CreateExpenseAsync(
        CreateExpenseRequest request, Guid userId, string role)
    {
        var businessId = await FinancialAccess.ResolveBusinessIdAsync(_context, userId);
        if (businessId is not Guid business)
            return ApiResponse<FinancialTransactionResponse>.Fail("FORBIDDEN", "No business scope for user.");

        if (request.Amount <= 0)
            return ApiResponse<FinancialTransactionResponse>.Fail(
                "VALIDATION_FAILED", "Amount must be greater than zero.");

        if (request.Amount > 100_000_000m)
            return ApiResponse<FinancialTransactionResponse>.Fail("VALIDATION_FAILED", "Amount is out of range.");

        var category = ExpenseCategories.Normalise(request.Category);
        if (category == null)
            return ApiResponse<FinancialTransactionResponse>.Fail(
                "VALIDATION_FAILED", "A valid expense category is required.");

        if (string.IsNullOrWhiteSpace(request.Description))
            return ApiResponse<FinancialTransactionResponse>.Fail(
                "VALIDATION_FAILED", "A description is required so the expense can be recognised later.");

        if (!FinancialWire.TryParseMethod(request.PaymentMethod ?? "cash", out var method))
            return ApiResponse<FinancialTransactionResponse>.Fail(
                "VALIDATION_FAILED", "Payment method is not recognised.");

        var expense = new FinancialTransaction
        {
            Id = Guid.NewGuid(),
            BusinessId = business,
            Type = FinancialTransactionType.Expense,
            // An expense is money the business decided to spend; the source axis describes where a
            // business TRANSACTION came from, so "other" is the honest value here.
            Source = TransactionSource.Other,
            PaymentMethod = method,
            // Money that has left the business is a fact, not a claim: it is verified on record.
            VerificationStatus = FinancialVerificationStatus.Verified,
            ReconciliationStatus = ReconciliationStatus.Unreconciled,
            Amount = request.Amount,
            Currency = string.IsNullOrWhiteSpace(request.Currency) ? "KES" : request.Currency!.Trim().ToUpperInvariant(),
            Reference = FinancialService.GenerateReference(),
            ExternalReference = FinancialLedgerService.Truncate(request.TransactionReference, 100),
            Category = category,
            Description = FinancialLedgerService.Truncate(request.Description!.Trim(), 500),
            CustomerName = string.IsNullOrWhiteSpace(request.Payee)
                ? null
                : FinancialLedgerService.Truncate(request.Payee.Trim(), 120),
            TransactionDate = request.TransactionDate?.ToUniversalTime() ?? DateTime.UtcNow,
            RecordedAt = DateTime.UtcNow,
            VerifiedAt = DateTime.UtcNow,
            VerifiedByUserId = userId,
            CreatedByUserId = userId,
            UpdatedAt = DateTime.UtcNow
        };

        await _context.FinancialTransactions.AddAsync(expense);
        await _uow.SaveChangesAsync();

        await FinancialAudit.WriteAsync(_context, _uow, _logger, business, "expense", expense.Id, "created",
            userId, expense.Reference,
            $"Recorded expense {expense.Currency} {expense.Amount:0.00} ({ExpenseCategories.Label(category)}).",
            null);

        _logger.LogInformation(
            "Expense {Reference} recorded by user {UserId} for business {BusinessId}: {Amount} {Currency} ({Category}).",
            expense.Reference, userId, business, expense.Amount, expense.Currency, category);

        return ApiResponse<FinancialTransactionResponse>.Ok(FinancialMapper.ToResponse(expense));
    }

    /// <summary>
    /// Edits an expense. The old values are captured before the change, so the audit trail shows
    /// exactly what was corrected — and a reconciled expense may not be edited at all, because by
    /// then it is a settled fact rather than a note to be tidied.
    /// </summary>
    public async Task<ApiResponse<FinancialTransactionResponse>> UpdateExpenseAsync(
        Guid id, UpdateExpenseRequest request, Guid userId, string role)
    {
        var businessId = await FinancialAccess.ResolveBusinessIdAsync(_context, userId);
        if (businessId is not Guid business)
            return ApiResponse<FinancialTransactionResponse>.Fail("FORBIDDEN", "No business scope for user.");

        var expense = await _context.FinancialTransactions
            .FirstOrDefaultAsync(t => t.Id == id && t.BusinessId == business);

        if (expense == null)
            return ApiResponse<FinancialTransactionResponse>.Fail("NOT_FOUND", "Expense not found.");

        if (expense.Type != FinancialTransactionType.Expense)
            return ApiResponse<FinancialTransactionResponse>.Fail(
                "INVALID_STATUS_TRANSITION", "Only an expense can be edited here.");

        if (expense.IsVoided)
            return ApiResponse<FinancialTransactionResponse>.Fail(
                "INVALID_STATUS_TRANSITION", "A voided expense cannot be edited.");

        if (expense.ReconciliationStatus == ReconciliationStatus.Matched)
            return ApiResponse<FinancialTransactionResponse>.Fail(
                "INVALID_STATUS_TRANSITION",
                "This expense has been reconciled against a statement, so it can no longer be edited.");

        var before = new
        {
            expense.Amount,
            expense.Category,
            expense.PaymentMethod,
            expense.Description,
            expense.CustomerName,
            expense.ExternalReference,
            expense.TransactionDate
        };

        if (request.Amount is decimal amount)
        {
            if (amount <= 0 || amount > 100_000_000m)
                return ApiResponse<FinancialTransactionResponse>.Fail("VALIDATION_FAILED", "Amount is out of range.");

            expense.Amount = amount;
        }

        if (!string.IsNullOrWhiteSpace(request.Category))
        {
            var category = ExpenseCategories.Normalise(request.Category);
            if (category == null)
                return ApiResponse<FinancialTransactionResponse>.Fail(
                    "VALIDATION_FAILED", "A valid expense category is required.");

            expense.Category = category;
        }

        if (!string.IsNullOrWhiteSpace(request.PaymentMethod))
        {
            if (!FinancialWire.TryParseMethod(request.PaymentMethod, out var method))
                return ApiResponse<FinancialTransactionResponse>.Fail(
                    "VALIDATION_FAILED", "Payment method is not recognised.");

            expense.PaymentMethod = method;
        }

        if (request.Description != null)
            expense.Description = FinancialLedgerService.Truncate(request.Description.Trim(), 500);

        if (request.Payee != null)
            expense.CustomerName = FinancialLedgerService.Truncate(request.Payee.Trim(), 120);

        if (request.TransactionReference != null)
            expense.ExternalReference = FinancialLedgerService.Truncate(request.TransactionReference.Trim(), 100);

        if (request.TransactionDate is DateTime date)
            expense.TransactionDate = date.ToUniversalTime();

        expense.UpdatedAt = DateTime.UtcNow;

        await _uow.SaveChangesAsync();

        var after = new
        {
            expense.Amount,
            expense.Category,
            expense.PaymentMethod,
            expense.Description,
            expense.CustomerName,
            expense.ExternalReference,
            expense.TransactionDate
        };

        await FinancialAudit.WriteAsync(_context, _uow, _logger, business, "expense", expense.Id, "updated",
            userId, expense.Reference,
            FinancialLedgerService.Truncate(request.Reason, 500) ?? "Expense updated.",
            FinancialService.Changes(before, after));

        _logger.LogInformation(
            "Expense {Reference} updated by user {UserId} (business {BusinessId}).", expense.Reference, userId, business);

        return ApiResponse<FinancialTransactionResponse>.Ok(FinancialMapper.ToResponse(expense));
    }

    /// <summary>
    /// Voids an expense: the row stays, the totals stop counting it, and the reason is recorded.
    /// Nothing is ever deleted, so "what did we think we spent, and why did that change?" survives.
    /// </summary>
    public async Task<ApiResponse<FinancialTransactionResponse>> VoidExpenseAsync(
        Guid id, VoidFinancialRecordRequest request, Guid userId, string role)
    {
        var businessId = await FinancialAccess.ResolveBusinessIdAsync(_context, userId);
        if (businessId is not Guid business)
            return ApiResponse<FinancialTransactionResponse>.Fail("FORBIDDEN", "No business scope for user.");

        if (string.IsNullOrWhiteSpace(request.Reason))
            return ApiResponse<FinancialTransactionResponse>.Fail(
                "VALIDATION_FAILED", "A reason is required when voiding an expense.");

        var expense = await _context.FinancialTransactions
            .FirstOrDefaultAsync(t => t.Id == id && t.BusinessId == business);

        if (expense == null)
            return ApiResponse<FinancialTransactionResponse>.Fail("NOT_FOUND", "Expense not found.");

        if (expense.Type != FinancialTransactionType.Expense)
            return ApiResponse<FinancialTransactionResponse>.Fail(
                "INVALID_STATUS_TRANSITION", "Only an expense can be voided here.");

        // Already voided: idempotent, and the FIRST reason is the one that stands.
        if (expense.IsVoided)
            return ApiResponse<FinancialTransactionResponse>.Ok(FinancialMapper.ToResponse(expense));

        expense.IsVoided = true;
        expense.VoidedAt = DateTime.UtcNow;
        expense.VoidedByUserId = userId;
        expense.VoidReason = FinancialLedgerService.Truncate(request.Reason!.Trim(), 500);
        expense.UpdatedAt = DateTime.UtcNow;

        await _uow.SaveChangesAsync();

        await FinancialAudit.WriteAsync(_context, _uow, _logger, business, "expense", expense.Id, "voided",
            userId, expense.Reference, expense.VoidReason,
            FinancialService.Changes(new { IsVoided = false }, new { IsVoided = true }));

        _logger.LogWarning(
            "Expense {Reference} voided by user {UserId} (business {BusinessId}): {Reason}",
            expense.Reference, userId, business, expense.VoidReason);

        return ApiResponse<FinancialTransactionResponse>.Ok(FinancialMapper.ToResponse(expense));
    }

    /// <summary>Expenses only, filtered and paged the same way as the ledger list.</summary>
    public async Task<ApiResponse<FinancialTransactionListResponse>> ListExpensesAsync(
        FinancialTransactionQuery query, Guid userId, string role)
    {
        var businessId = await FinancialAccess.ResolveBusinessIdAsync(_context, userId);
        if (businessId is not Guid business)
            return ApiResponse<FinancialTransactionListResponse>.Fail("FORBIDDEN", "No business scope for user.");

        // An expense list can never leak income, whatever the caller puts in `type`.
        query.Type = FinancialWire.ToWire(FinancialTransactionType.Expense);

        var filtered = FinancialQueries.ApplyFilters(
            _context, _context.FinancialTransactions.AsNoTracking(), query, business);
        var (page, pageSize, skip) = FinancialQueries.Paging(query.Page, query.PageSize);

        var total = await filtered.CountAsync();

        var amounts = await filtered
            .Where(t => !t.IsVoided)
            .Select(t => new { t.Amount, t.Category })
            .ToListAsync();

        var expenseTotal = amounts.Sum(a => a.Amount);

        // The category panel and the list read the SAME filtered query, so a total can never
        // describe a set of rows the register does not show.
        var categoryBreakdown = amounts
            .GroupBy(a => a.Category ?? "other")
            .Select(group => new FinancialBreakdownItem
            {
                Key = group.Key,
                Label = ExpenseCategories.Label(group.Key),
                Amount = group.Sum(a => a.Amount),
                Count = group.Count(),
                Percentage = expenseTotal <= 0
                    ? 0
                    : Math.Round(group.Sum(a => a.Amount) / expenseTotal * 100m, 1, MidpointRounding.AwayFromZero)
            })
            .OrderByDescending(item => item.Amount)
            .ToList();

        var rows = await FinancialQueries.ApplySort(filtered, query.Sort, query.SortDirection)
            .Skip(skip)
            .Take(pageSize)
            .ToListAsync();

        // "This month" is the business's own calendar month, not UTC's, and it is deliberately
        // independent of whatever filter the owner has set — a headline that moved when a category
        // was picked would not be a headline. Voided rows never count.
        var timeZone = await FinancialAccess.ResolveTimeZoneAsync(_context, business);
        var month = FinancialPeriod.Resolve(FinancialPeriod.ThisMonth, null, null, timeZone);

        var monthAmounts = await _context.FinancialTransactions.AsNoTracking()
            .Where(t => t.BusinessId == business
                        && t.Type == FinancialTransactionType.Expense
                        && !t.IsVoided
                        && t.TransactionDate >= month.FromUtc
                        && t.TransactionDate < month.ToUtc)
            .Select(t => t.Amount)
            .ToListAsync();

        var names = await FinancialAccess.ResolveUserNamesAsync(_context, rows.Select(r => r.CreatedByUserId));

        return ApiResponse<FinancialTransactionListResponse>.Ok(new FinancialTransactionListResponse
        {
            Items = rows.Select(r => FinancialMapper.ToResponse(
                r,
                createdByName: r.CreatedByUserId is Guid recorder ? names.GetValueOrDefault(recorder) : null)).ToList(),
            Page = page,
            PageSize = pageSize,
            TotalCount = total,
            ExpenseTotal = expenseTotal,
            CurrentMonthExpenseTotal = monthAmounts.Sum(),
            ExpenseCount = amounts.Count,
            CategoryBreakdown = categoryBreakdown,
            TotalPages = pageSize <= 0 ? 1 : (int)Math.Ceiling(total / (double)pageSize)
        });
    }
}
