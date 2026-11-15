using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Application.Services;

/// <summary>
/// The write path that keeps the business ledger in step with the payment module.
///
/// <para><b>Why a separate seam.</b> Payment settlement happens in two places — the service
/// (cash confirmation, manual M-Pesa verification) and the callback processor (provider
/// confirmation, reversals) — and both must produce the same ledger result exactly once.
/// Centralising it here means the payment code carries no financial rules, and the
/// idempotency key (<see cref="FinancialTransaction.SourcePaymentId"/>) lives in one place.</para>
///
/// <para><b>Failure policy.</b> Ledger writes are non-fatal by design: a settlement that has
/// already been recorded must never be rolled back because the ledger insert failed. The
/// payment path logs and continues, exactly as the commission ledger does.</para>
/// </summary>
public interface IFinancialLedgerService
{
    /// <summary>
    /// Recognise a settled payment as verified income. Idempotent: the unique
    /// <c>source_payment_id</c> index (plus a pre-check) guarantees one row per payment, so
    /// replayed callbacks and repeated confirmations cannot double-count.
    /// </summary>
    Task<FinancialTransaction?> EnsureIncomeForPaymentAsync(Payment payment, CancellationToken cancellationToken = default);

    /// <summary>
    /// Record the refund that accompanies a payment reversal. The original income row is left
    /// untouched; the refund is a linked row so both sides stay provable. Idempotent per
    /// payment: a replayed reversal cannot produce two refunds.
    /// </summary>
    Task<FinancialTransaction?> EnsureRefundForReversedPaymentAsync(
        Payment payment, string reason, CancellationToken cancellationToken = default);
}

/// <summary>
/// Business income and the transaction list: recording money that arrived outside Punched,
/// reading the ledger, deciding on recorded claims, refunding and reconciling.
/// </summary>
public interface IFinancialService
{
    /// <summary>Records standalone business income (walk-in, direct M-Pesa, product sale, cash, …).</summary>
    Task<ApiResponse<FinancialTransactionResponse>> RecordIncomeAsync(
        RecordIncomeRequest request, Guid userId, string role);

    /// <summary>One ledger row, tenant-scoped.</summary>
    Task<ApiResponse<FinancialTransactionResponse>> GetTransactionAsync(Guid id, Guid userId, string role);

    /// <summary>Filtered, sorted, server-paginated ledger rows plus the totals for that filter.</summary>
    Task<ApiResponse<FinancialTransactionListResponse>> ListTransactionsAsync(
        FinancialTransactionQuery query, Guid userId, string role);

    /// <summary>Confirms a recorded claim (manual M-Pesa income, or a flagged row).</summary>
    Task<ApiResponse<FinancialTransactionResponse>> VerifyTransactionAsync(
        Guid id, VerifyFinancialTransactionRequest request, Guid userId, string role);

    /// <summary>Refuses a recorded claim, with the reason kept on the row and in the audit trail.</summary>
    Task<ApiResponse<FinancialTransactionResponse>> RejectTransactionAsync(
        Guid id, RejectFinancialTransactionRequest request, Guid userId, string role);

    /// <summary>Records a full or partial refund against an income row. Never mutates the original.</summary>
    Task<ApiResponse<FinancialTransactionResponse>> RecordRefundAsync(
        RecordRefundRequest request, Guid userId, string role);

    /// <summary>Owner reconciliation decision on a transaction (matched/discrepancy/excluded/unreconciled).</summary>
    Task<ApiResponse<FinancialTransactionResponse>> ReconcileTransactionAsync(
        Guid id, ReconcileTransactionRequest request, Guid userId, string role);

    /// <summary>A record's audit trail, newest first.</summary>
    Task<ApiResponse<List<FinancialAuditLogResponse>>> GetAuditTrailAsync(
        Guid id, Guid userId, string role);
}

/// <summary>Expenses: recorded, edited with history, and voided — never deleted.</summary>
public interface IExpenseService
{
    Task<ApiResponse<FinancialTransactionResponse>> CreateExpenseAsync(
        CreateExpenseRequest request, Guid userId, string role);

    Task<ApiResponse<FinancialTransactionResponse>> UpdateExpenseAsync(
        Guid id, UpdateExpenseRequest request, Guid userId, string role);

    Task<ApiResponse<FinancialTransactionResponse>> VoidExpenseAsync(
        Guid id, VoidFinancialRecordRequest request, Guid userId, string role);

    Task<ApiResponse<FinancialTransactionListResponse>> ListExpensesAsync(
        FinancialTransactionQuery query, Guid userId, string role);

    /// <summary>The predefined category list the UI offers.</summary>
    ApiResponse<List<ExpenseCategoryResponse>> GetCategories();
}

/// <summary>Overview, cashbook and analytics — all read from the ledger, never re-derived from payments.</summary>
public interface IFinancialAnalyticsService
{
    /// <summary>The owner-facing financial overview for a period.</summary>
    Task<ApiResponse<FinancialOverviewResponse>> GetOverviewAsync(
        string? period, DateTime? from, DateTime? to, Guid userId, string role);

    /// <summary>The cashbook: every recorded movement in the period, plus the net.</summary>
    Task<ApiResponse<CashbookResponse>> GetCashbookAsync(
        FinancialTransactionQuery query, Guid userId, string role);

    /// <summary>Revenue, payment, expense and performance analytics for a period.</summary>
    Task<ApiResponse<FinancialAnalyticsResponse>> GetAnalyticsAsync(
        string? period, DateTime? from, DateTime? to, Guid userId, string role);
}

/// <summary>M-Pesa statement import and reconciliation (no direct Safaricom integration is assumed).</summary>
public interface IMpesaReconciliationService
{
    /// <summary>The reconciliation workspace for the business's latest import.</summary>
    Task<ApiResponse<ReconciliationWorkspaceResponse>> GetWorkspaceAsync(Guid userId, string role);

    /// <summary>Imports a statement and matches it automatically against the ledger and payments.</summary>
    Task<ApiResponse<ReconciliationWorkspaceResponse>> ImportStatementAsync(
        ImportMpesaStatementRequest request, Guid userId, string role);

    /// <summary>Manual match (or exclusion) of one imported line.</summary>
    Task<ApiResponse<MpesaStatementLineResponse>> MatchLineAsync(
        Guid lineId, MatchStatementLineRequest request, Guid userId, string role);
}

/// <summary>
/// Filters for the ledger list, the expense list and the cashbook. Server-side paging and
/// sorting only: a client must never receive the whole ledger to filter it itself.
/// </summary>
public class FinancialTransactionQuery
{
    public string? Type { get; set; }

    /// <summary>
    /// <c>incoming</c> | <c>outgoing</c> (also accepts <c>in</c>/<c>out</c>,
    /// <c>money_in</c>/<c>money_out</c>). Filtering by direction is how the cashbook's Money In /
    /// Money Out tabs work — and it is applied server-side, so the totals and the page agree.
    /// </summary>
    public string? Direction { get; set; }

    public string? Source { get; set; }
    public string? PaymentMethod { get; set; }
    public string? VerificationStatus { get; set; }
    public string? ReconciliationStatus { get; set; }

    /// <summary>
    /// Expense category key (see <see cref="ExpenseCategories"/>). Only meaningful on the expense
    /// list; an unrecognised key matches nothing rather than everything, so a filter can never
    /// silently widen into a report of what the business spent.
    /// </summary>
    public string? Category { get; set; }

    public Guid? CustomerId { get; set; }
    public Guid? AppointmentId { get; set; }
    public decimal? MinAmount { get; set; }
    public decimal? MaxAmount { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public string? Search { get; set; }

    /// <summary>Who recorded the row (the cashbook's "recorded by" filter).</summary>
    public Guid? RecordedByUserId { get; set; }

    /// <summary>
    /// The display name of whoever recorded the row (contains match). Resolved through the Users
    /// table with an EXISTS subquery rather than a join, so the register stays one query wide and
    /// the filter still runs in the database.
    /// </summary>
    public string? RecordedByName { get; set; }

    /// <summary>
    /// Customer or payee name (contains match). One filter for both sides of the book: a receipt
    /// names a customer, an expense names whoever was paid, and both live on
    /// <c>CustomerName</c>.
    /// </summary>
    public string? Payee { get; set; }

    /// <summary>
    /// Whether voided rows are LISTED (they never contribute to a total — every sum checks
    /// <c>IsVoided</c>). The cashbook defaults to hiding rows that moved nothing; the expense
    /// register defaults to showing them, because an audit needs the evidence.
    /// </summary>
    public bool IncludeVoided { get; set; } = true;

    /// <summary>Ordering: <c>date</c> (default, newest first), <c>amount</c>, <c>reference</c>.</summary>
    public string? Sort { get; set; }

    /// <summary><c>asc</c> or <c>desc</c> — the SORT order.</summary>
    public string? SortDirection { get; set; }

    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
}
