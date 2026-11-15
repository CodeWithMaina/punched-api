using System.Text.Json.Serialization;

namespace PunchedApi.Application.DTOs;

/// <summary>One ledger row as the API presents it.</summary>
public class FinancialTransactionResponse
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("businessId")]
    public Guid BusinessId { get; set; }

    /// <summary><c>income</c> | <c>expense</c> | <c>refund</c> | <c>adjustment</c>.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("paymentMethod")]
    public string PaymentMethod { get; set; } = string.Empty;

    [JsonPropertyName("verificationStatus")]
    public string VerificationStatus { get; set; } = string.Empty;

    [JsonPropertyName("reconciliationStatus")]
    public string ReconciliationStatus { get; set; } = string.Empty;

    [JsonPropertyName("amount")]
    public decimal Amount { get; set; }

    /// <summary>Signed amount for display: positive for income, negative for expense/refund.</summary>
    [JsonPropertyName("signedAmount")]
    public decimal SignedAmount { get; set; }

    /// <summary>
    /// <c>incoming</c> | <c>outgoing</c> — which way the money moved. Stated by the server rather
    /// than derived by the client from the type, so an expense paid by M-Pesa and a refund against
    /// an income row are both unambiguously money out.
    /// </summary>
    [JsonPropertyName("direction")]
    public string Direction { get; set; } = string.Empty;

    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "KES";

    [JsonPropertyName("reference")]
    public string Reference { get; set; } = string.Empty;

    [JsonPropertyName("externalReference")]
    public string? ExternalReference { get; set; }

    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("categoryLabel")]
    public string? CategoryLabel { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("customerId")]
    public Guid? CustomerId { get; set; }

    [JsonPropertyName("customerName")]
    public string? CustomerName { get; set; }

    [JsonPropertyName("appointmentId")]
    public Guid? AppointmentId { get; set; }

    [JsonPropertyName("sourcePaymentId")]
    public Guid? SourcePaymentId { get; set; }

    [JsonPropertyName("refundOfTransactionId")]
    public Guid? RefundOfTransactionId { get; set; }

    [JsonPropertyName("refundReason")]
    public string? RefundReason { get; set; }

    [JsonPropertyName("refundReference")]
    public string? RefundReference { get; set; }

    [JsonPropertyName("transactionDate")]
    public DateTime TransactionDate { get; set; }

    [JsonPropertyName("recordedAt")]
    public DateTime RecordedAt { get; set; }

    [JsonPropertyName("verifiedAt")]
    public DateTime? VerifiedAt { get; set; }

    [JsonPropertyName("verifiedByUserId")]
    public Guid? VerifiedByUserId { get; set; }

    [JsonPropertyName("rejectionReason")]
    public string? RejectionReason { get; set; }

    [JsonPropertyName("createdByUserId")]
    public Guid? CreatedByUserId { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; }

    [JsonPropertyName("isVoided")]
    public bool IsVoided { get; set; }

    [JsonPropertyName("voidedAt")]
    public DateTime? VoidedAt { get; set; }

    [JsonPropertyName("voidReason")]
    public string? VoidReason { get; set; }

    [JsonPropertyName("reconciledAt")]
    public DateTime? ReconciledAt { get; set; }

    [JsonPropertyName("statementLineId")]
    public Guid? StatementLineId { get; set; }

    [JsonPropertyName("reconciliationNote")]
    public string? ReconciliationNote { get; set; }

    /// <summary>How much of a refunded income row is still refundable (0 when not refundable).</summary>
    [JsonPropertyName("refundableAmount")]
    public decimal RefundableAmount { get; set; }

    /// <summary>Owner-recorded claim vs confirmed evidence.</summary>
    [JsonPropertyName("isOwnerRecorded")]
    public bool IsOwnerRecorded { get; set; }

    /// <summary>
    /// Display name of whoever recorded the row, resolved for the page (never one query per row).
    /// The row itself keeps only the id, so a later rename cannot rewrite the register's history.
    /// </summary>
    [JsonPropertyName("createdByName")]
    public string? CreatedByName { get; set; }
}

/// <summary>A page of ledger rows plus the totals for the filter that produced them.</summary>
public class FinancialTransactionListResponse
{
    [JsonPropertyName("items")]
    public List<FinancialTransactionResponse> Items { get; set; } = new();

    [JsonPropertyName("page")]
    public int Page { get; set; }

    [JsonPropertyName("pageSize")]
    public int PageSize { get; set; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }

    /// <summary>Sum of the filtered, verified income rows (all pages, not just this one).</summary>
    [JsonPropertyName("verifiedIncomeTotal")]
    public decimal VerifiedIncomeTotal { get; set; }

    /// <summary>Sum of the filtered expenses.</summary>
    [JsonPropertyName("expenseTotal")]
    public decimal ExpenseTotal { get; set; }

    /// <summary>Sum of the filtered refunds.</summary>
    [JsonPropertyName("refundTotal")]
    public decimal RefundTotal { get; set; }

    /// <summary>
    /// Expenses recorded in the current calendar month (business-local), voided rows excluded.
    /// Only the expense register populates it; it is 0 on the general ledger list.
    /// </summary>
    [JsonPropertyName("currentMonthExpenseTotal")]
    public decimal CurrentMonthExpenseTotal { get; set; }

    /// <summary>Total pages for the FILTERED result set, so a client never guesses.</summary>
    [JsonPropertyName("totalPages")]
    public int TotalPages { get; set; }
}

/// <summary>One labelled slice of a breakdown (revenue source, payment method, category, staff).</summary>
public class FinancialBreakdownItem
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("amount")]
    public decimal Amount { get; set; }

    [JsonPropertyName("count")]
    public int Count { get; set; }

    /// <summary>Share of the breakdown total, 0–100, rounded to 1dp.</summary>
    [JsonPropertyName("percentage")]
    public decimal Percentage { get; set; }
}

/// <summary>The money summary for a period. Every figure is defined in one place (the analytics service).</summary>
public class FinancialOverviewTotals
{
    /// <summary>Income that has been confirmed. Pending and rejected rows are excluded.</summary>
    [JsonPropertyName("verifiedIncome")]
    public decimal VerifiedIncome { get; set; }

    /// <summary>Amount recorded but not yet confirmed — never part of verified income.</summary>
    [JsonPropertyName("pendingVerification")]
    public decimal PendingVerification { get; set; }

    [JsonPropertyName("pendingVerificationCount")]
    public int PendingVerificationCount { get; set; }

    /// <summary>Payments still waiting on the customer/owner, derived from the payments module.</summary>
    [JsonPropertyName("paymentsAwaitingVerification")]
    public decimal PaymentsAwaitingVerification { get; set; }

    /// <summary>Expenses recorded in the period (voided rows excluded).</summary>
    [JsonPropertyName("totalExpenses")]
    public decimal TotalExpenses { get; set; }

    /// <summary>Refunds recorded in the period (voided rows excluded).</summary>
    [JsonPropertyName("totalRefunds")]
    public decimal TotalRefunds { get; set; }

    /// <summary>
    /// Net cash movement: recorded verified inflows minus recorded outflows (expenses +
    /// refunds). Deliberately NOT described as an account balance — Punched has not seen the
    /// business's bank or M-Pesa balance.
    /// </summary>
    [JsonPropertyName("netCashMovement")]
    public decimal NetCashMovement { get; set; }

    /// <summary>What customers still owe across live appointments (never derived from all-time income).</summary>
    [JsonPropertyName("outstandingBalances")]
    public decimal OutstandingBalances { get; set; }

    [JsonPropertyName("outstandingAppointmentCount")]
    public int OutstandingAppointmentCount { get; set; }

    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "KES";
}

/// <summary>The financial overview: one screen's worth of answers for a period.</summary>
public class FinancialOverviewResponse
{
    /// <summary>The resolved period key (<c>today</c>, <c>last_7_days</c>, <c>custom</c>, …).</summary>
    [JsonPropertyName("period")]
    public string Period { get; set; } = string.Empty;

    /// <summary>Inclusive period start (UTC), after the business's timezone was applied.</summary>
    [JsonPropertyName("from")]
    public DateTime From { get; set; }

    /// <summary>Exclusive period end (UTC).</summary>
    [JsonPropertyName("to")]
    public DateTime To { get; set; }

    [JsonPropertyName("totals")]
    public FinancialOverviewTotals Totals { get; set; } = new();

    /// <summary>Verified income by origin (appointment, walk-in, product, other).</summary>
    [JsonPropertyName("revenueBreakdown")]
    public List<FinancialBreakdownItem> RevenueBreakdown { get; set; } = new();

    /// <summary>Verified income by payment method (Till, Paybill, Send Money, cash, bank, card, other).</summary>
    [JsonPropertyName("paymentMethodBreakdown")]
    public List<FinancialBreakdownItem> PaymentMethodBreakdown { get; set; } = new();

    /// <summary>Expenses by category.</summary>
    [JsonPropertyName("expenseBreakdown")]
    public List<FinancialBreakdownItem> ExpenseBreakdown { get; set; } = new();

    /// <summary>The newest ledger rows in the period, for the activity list.</summary>
    [JsonPropertyName("recentActivity")]
    public List<FinancialTransactionResponse> RecentActivity { get; set; } = new();
}

/// <summary>One movement in the cashbook, from whichever ledger row it came from.</summary>
public class CashbookEntryResponse
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("date")]
    public DateTime Date { get; set; }

    /// <summary><c>income</c> | <c>expense</c> | <c>refund</c> | <c>adjustment</c>.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary><c>incoming</c> | <c>outgoing</c> — the direction money moved.</summary>
    [JsonPropertyName("direction")]
    public string Direction { get; set; } = string.Empty;

    [JsonPropertyName("amount")]
    public decimal Amount { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("reference")]
    public string Reference { get; set; } = string.Empty;

    [JsonPropertyName("externalReference")]
    public string? ExternalReference { get; set; }

    [JsonPropertyName("paymentMethod")]
    public string PaymentMethod { get; set; } = string.Empty;

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("customerName")]
    public string? CustomerName { get; set; }

    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("verificationStatus")]
    public string VerificationStatus { get; set; } = string.Empty;

    [JsonPropertyName("isVoided")]
    public bool IsVoided { get; set; }

    /// <summary>
    /// Cumulative recorded net movement up to and including this entry, in chronological order.
    ///
    /// <para>It starts at zero at the beginning of the reported window (Punched has never seen an
    /// opening balance, so none is invented) and is therefore the running NET of what was recorded
    /// in the window — NOT the balance of any bank, M-Pesa or cash account.</para>
    /// </summary>
    [JsonPropertyName("runningBalance")]
    public decimal RunningBalance { get; set; }

    /// <summary>Display name of whoever recorded the entry, resolved for the page.</summary>
    [JsonPropertyName("recordedByName")]
    public string? RecordedByName { get; set; }
}

/// <summary>The cashbook: every recorded movement for a period, plus what it nets to.</summary>
public class CashbookResponse
{
    [JsonPropertyName("period")]
    public string Period { get; set; } = string.Empty;

    [JsonPropertyName("from")]
    public DateTime From { get; set; }

    [JsonPropertyName("to")]
    public DateTime To { get; set; }

    [JsonPropertyName("entries")]
    public List<CashbookEntryResponse> Entries { get; set; } = new();

    [JsonPropertyName("page")]
    public int Page { get; set; }

    [JsonPropertyName("pageSize")]
    public int PageSize { get; set; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }

    /// <summary>Recorded verified inflows in the period.</summary>
    [JsonPropertyName("totalInflows")]
    public decimal TotalInflows { get; set; }

    /// <summary>Recorded outflows: expenses plus refunds.</summary>
    [JsonPropertyName("totalOutflows")]
    public decimal TotalOutflows { get; set; }

    [JsonPropertyName("expenses")]
    public decimal Expenses { get; set; }

    [JsonPropertyName("refunds")]
    public decimal Refunds { get; set; }

    /// <summary>Total recorded inflows − total recorded outflows. Not a bank balance.</summary>
    [JsonPropertyName("netCashMovement")]
    public decimal NetCashMovement { get; set; }

    /// <summary>Total pages for the FILTERED result set.</summary>
    [JsonPropertyName("totalPages")]
    public int TotalPages { get; set; }

    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "KES";
}
