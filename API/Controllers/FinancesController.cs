using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PunchedApi.API.Filters;
using PunchedApi.Application.Authorization;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;

namespace PunchedApi.API.Controllers;

/// <summary>
/// Financial management — the business's own books: income from any source, expenses, the cashbook,
/// refunds, M-Pesa statement reconciliation and financial analytics.
///
/// <para>Base route: <c>/v1/finances</c>. Convention mirrors PaymentsController: <c>[Authorize]</c> at
/// controller level, membership policies per action, <c>[RequirePermission]</c> for the fine-grained
/// money decisions, the <c>ApiResponse</c> envelope and <c>MapFailure</c>.</para>
///
/// <para><b>No business id is ever read from the request.</b> Every service resolves the caller's
/// business from their active membership, so a body carrying another tenant's id simply finds
/// nothing — and a caller cannot point the ledger at somebody else's money.</para>
/// </summary>
[ApiController]
[Route("v1/finances")]
[Produces("application/json")]
[Authorize]
[RequireModule("financials")]
[EnableRateLimiting("general")]
public class FinancesController : ControllerBase
{
    private readonly IFinancialService _finances;
    private readonly IExpenseService _expenses;
    private readonly IFinancialAnalyticsService _analytics;
    private readonly IMpesaReconciliationService _reconciliation;
    private readonly IPaymentService _payments;

    public FinancesController(
        IFinancialService finances,
        IExpenseService expenses,
        IFinancialAnalyticsService analytics,
        IMpesaReconciliationService reconciliation,
        IPaymentService payments)
    {
        _finances = finances;
        _expenses = expenses;
        _analytics = analytics;
        _reconciliation = reconciliation;
        _payments = payments;
    }

    // ═══════════════════════════════════════════════════════════
    //  OVERVIEW
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// The financial overview for a period: verified income, pending verification, expenses, net cash
    /// movement, outstanding balances and refunds, plus the breakdowns and recent activity.
    /// </summary>
    [HttpGet("overview")]
    [Authorize(Policy = MembershipPolicies.OwnerOrStaff)]
    [RequirePermission("finances.view")]
    [ProducesResponseType(typeof(ApiResponse<FinancialOverviewResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Overview(
        [FromQuery] string period = "last_30_days",
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null)
    {
        var (userId, role) = GetIdentity();
        if (userId == null) return Unauthorized();

        var result = await _analytics.GetOverviewAsync(period, from, to, userId.Value, role!);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>The cashbook: every recorded movement in the period, and what it nets to.</summary>
    [HttpGet("cashbook")]
    [Authorize(Policy = MembershipPolicies.OwnerOrStaff)]
    [RequirePermission("finances.view")]
    [ProducesResponseType(typeof(ApiResponse<CashbookResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Cashbook(
        [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] string? type,
        [FromQuery] string? direction, [FromQuery] string? source, [FromQuery] string? category,
        [FromQuery] string? paymentMethod, [FromQuery] string? search,
        [FromQuery] Guid? recordedByUserId, [FromQuery] string? recordedBy, [FromQuery] string? customer,
        [FromQuery] decimal? minAmount, [FromQuery] decimal? maxAmount,
        [FromQuery] bool includeVoided = false,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25)
    {
        var (userId, role) = GetIdentity();
        if (userId == null) return Unauthorized();

        var result = await _analytics.GetCashbookAsync(new FinancialTransactionQuery
        {
            From = from, To = to, Type = type, Direction = direction, Source = source,
            Category = category, PaymentMethod = paymentMethod, Search = search,
            RecordedByUserId = recordedByUserId, RecordedByName = recordedBy, Payee = customer,
            MinAmount = minAmount, MaxAmount = maxAmount,
            // The book shows MOVEMENTS. A voided row moved nothing, so it is excluded unless the
            // owner explicitly asks to audit it.
            IncludeVoided = includeVoided,
            Page = page, PageSize = pageSize
        }, userId.Value, role!);

        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>Revenue, payment, expense and performance analytics for a period.</summary>
    [HttpGet("analytics")]
    [Authorize(Policy = MembershipPolicies.Owner)]
    [RequirePermission("finances.analytics.view")]
    [ProducesResponseType(typeof(ApiResponse<FinancialAnalyticsResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Analytics(
        [FromQuery] string period = "last_30_days",
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null)
    {
        var (userId, role) = GetIdentity();
        if (userId == null) return Unauthorized();

        var result = await _analytics.GetAnalyticsAsync(period, from, to, userId.Value, role!);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>The predefined expense categories the UI offers.</summary>
    [HttpGet("expense-categories")]
    [Authorize(Policy = MembershipPolicies.OwnerOrStaff)]
    [RequirePermission("finances.view")]
    [ProducesResponseType(typeof(ApiResponse<List<ExpenseCategoryResponse>>), StatusCodes.Status200OK)]
    public IActionResult ExpenseCategories()
    {
        var result = _expenses.GetCategories();
        return result.Success ? Ok(result) : MapFailure(result);
    }

    // ═══════════════════════════════════════════════════════════
    //  TRANSACTIONS
    // ═══════════════════════════════════════════════════════════

    /// <summary>The ledger, filtered, sorted and paged server-side, with the totals for that filter.</summary>
    [HttpGet("transactions")]
    [Authorize(Policy = MembershipPolicies.OwnerOrStaff)]
    [RequirePermission("finances.view")]
    [ProducesResponseType(typeof(ApiResponse<FinancialTransactionListResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Transactions(
        [FromQuery] string? type, [FromQuery] string? source, [FromQuery] string? paymentMethod,
        [FromQuery] string? verificationStatus, [FromQuery] string? reconciliationStatus,
        [FromQuery] Guid? customerId, [FromQuery] Guid? appointmentId,
        [FromQuery] decimal? minAmount, [FromQuery] decimal? maxAmount,
        [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] string? search,
        [FromQuery] string? sort, [FromQuery] string? sortDirection = null,
        [FromQuery] string? direction = null,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25)
    {
        var (userId, role) = GetIdentity();
        if (userId == null) return Unauthorized();

        // `direction` used to carry the SORT order and now carries the MONEY direction, so the old
        // meaning is still honoured when the value can only be a sort order — a money direction is
        // never "asc". Existing callers keep working; new ones can be explicit.
        var orderDirection = !string.IsNullOrWhiteSpace(sortDirection)
            ? sortDirection
            : IsSortOrder(direction) ? direction : null;

        var moneyDirection = IsSortOrder(direction) ? null : direction;

        var result = await _finances.ListTransactionsAsync(new FinancialTransactionQuery
        {
            Type = type,
            Direction = moneyDirection,
            Source = source,
            PaymentMethod = paymentMethod,
            VerificationStatus = verificationStatus,
            ReconciliationStatus = reconciliationStatus,
            CustomerId = customerId,
            AppointmentId = appointmentId,
            MinAmount = minAmount,
            MaxAmount = maxAmount,
            From = from,
            To = to,
            Search = search,
            Sort = sort,
            SortDirection = orderDirection,
            Page = page,
            PageSize = pageSize
        }, userId.Value, role!);

        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>True when a value can only be an ordering: "asc"/"desc".</summary>
    private static bool IsSortOrder(string? value) =>
        string.Equals(value, "asc", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "desc", StringComparison.OrdinalIgnoreCase);

    /// <summary>One ledger row, tenant-scoped.</summary>
    [HttpGet("transactions/{id:guid}")]
    [Authorize(Policy = MembershipPolicies.OwnerOrStaff)]
    [RequirePermission("finances.view")]
    [ProducesResponseType(typeof(ApiResponse<FinancialTransactionResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Transaction(Guid id)
    {
        var (userId, role) = GetIdentity();
        if (userId == null) return Unauthorized();

        var result = await _finances.GetTransactionAsync(id, userId.Value, role!);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>
    /// Record business income that did not come from a Punched appointment: a walk-in, a direct
    /// M-Pesa Till/Paybill/Send Money payment, a product sale, cash, a bank transfer or other income.
    /// No appointment is required, and the customer is optional.
    /// </summary>
    [HttpPost("income")]
    [Authorize(Policy = MembershipPolicies.OwnerOrStaff)]
    [RequirePermission("finances.record_income")]
    [ProducesResponseType(typeof(ApiResponse<FinancialTransactionResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> RecordIncome([FromBody] RecordIncomeRequest request)
    {
        var (userId, role) = GetIdentity();
        if (userId == null) return Unauthorized();

        var result = await _finances.RecordIncomeAsync(request, userId.Value, role!);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>Confirm a recorded claim (manual M-Pesa income, or a flagged row).</summary>
    [HttpPost("transactions/{id:guid}/verify")]
    [Authorize(Policy = MembershipPolicies.OwnerOrStaff)]
    [RequirePermission("finances.verify")]
    [ProducesResponseType(typeof(ApiResponse<FinancialTransactionResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Verify(Guid id, [FromBody] VerifyFinancialTransactionRequest? request)
    {
        var (userId, role) = GetIdentity();
        if (userId == null) return Unauthorized();

        var result = await _finances.VerifyTransactionAsync(
            id, request ?? new VerifyFinancialTransactionRequest(), userId.Value, role!);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>Refuse a recorded claim. The reason is required.</summary>
    [HttpPost("transactions/{id:guid}/reject")]
    [Authorize(Policy = MembershipPolicies.OwnerOrStaff)]
    [RequirePermission("finances.verify")]
    [ProducesResponseType(typeof(ApiResponse<FinancialTransactionResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Reject(Guid id, [FromBody] RejectFinancialTransactionRequest? request)
    {
        var (userId, role) = GetIdentity();
        if (userId == null) return Unauthorized();

        var result = await _finances.RejectTransactionAsync(
            id, request ?? new RejectFinancialTransactionRequest(), userId.Value, role!);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>
    /// Void a hand-recorded income row. Not a delete: the row stays, marked voided with a reason,
    /// and stops counting. Income that a payment produced is refused here — it is reversed through
    /// the payment so the two records cannot disagree.
    /// </summary>
    [HttpPost("transactions/{id:guid}/void")]
    [Authorize(Policy = MembershipPolicies.OwnerOrStaff)]
    [RequirePermission("finances.record_income")]
    [ProducesResponseType(typeof(ApiResponse<FinancialTransactionResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Void(Guid id, [FromBody] VoidFinancialRecordRequest? request)
    {
        var (userId, role) = GetIdentity();
        if (userId == null) return Unauthorized();

        var result = await _finances.VoidTransactionAsync(
            id, request ?? new VoidFinancialRecordRequest(), userId.Value, role!);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>Record the owner's reconciliation verdict on a transaction (independent of verification).</summary>
    [HttpPost("transactions/{id:guid}/reconcile")]
    [Authorize(Policy = MembershipPolicies.OwnerOrStaff)]
    [RequirePermission("finances.reconcile")]
    [ProducesResponseType(typeof(ApiResponse<FinancialTransactionResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Reconcile(Guid id, [FromBody] ReconcileTransactionRequest? request)
    {
        var (userId, role) = GetIdentity();
        if (userId == null) return Unauthorized();

        var result = await _finances.ReconcileTransactionAsync(
            id, request ?? new ReconcileTransactionRequest(), userId.Value, role!);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>A transaction's audit trail, newest first.</summary>
    [HttpGet("transactions/{id:guid}/audit")]
    [Authorize(Policy = MembershipPolicies.OwnerOrStaff)]
    [RequirePermission("finances.view")]
    [ProducesResponseType(typeof(ApiResponse<List<FinancialAuditLogResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> AuditTrail(Guid id)
    {
        var (userId, role) = GetIdentity();
        if (userId == null) return Unauthorized();

        var result = await _finances.GetAuditTrailAsync(id, userId.Value, role!);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    // ═══════════════════════════════════════════════════════════
    //  REFUNDS
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// Record a full or partial refund against an income row. The original is never modified: the
    /// refund is its own linked record, so both sides stay provable.
    /// </summary>
    [HttpPost("refunds")]
    [Authorize(Policy = MembershipPolicies.OwnerOrStaff)]
    [RequirePermission("finances.refund")]
    [ProducesResponseType(typeof(ApiResponse<FinancialTransactionResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RecordRefund([FromBody] RecordRefundRequest request)
    {
        var (userId, role) = GetIdentity();
        if (userId == null) return Unauthorized();

        var result = await _finances.RecordRefundAsync(request, userId.Value, role!);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    // ═══════════════════════════════════════════════════════════
    //  EXPENSES
    // ═══════════════════════════════════════════════════════════

    /// <summary>The business's expenses, filtered and paged (income is never included here).</summary>
    [HttpGet("expenses")]
    [Authorize(Policy = MembershipPolicies.OwnerOrStaff)]
    [RequirePermission("finances.view")]
    [ProducesResponseType(typeof(ApiResponse<FinancialTransactionListResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Expenses(
        [FromQuery] string? category,
        [FromQuery] string? paymentMethod, [FromQuery] decimal? minAmount, [FromQuery] decimal? maxAmount,
        [FromQuery] DateTime? from, [FromQuery] DateTime? to, [FromQuery] string? search,
        [FromQuery] string? sort, [FromQuery] string? sortDirection,
        [FromQuery] Guid? recordedByUserId, [FromQuery] string? payee,
        [FromQuery] bool excludeVoided = false,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25)
    {
        var (userId, role) = GetIdentity();
        if (userId == null) return Unauthorized();

        var result = await _expenses.ListExpensesAsync(new FinancialTransactionQuery
        {
            Category = category,
            PaymentMethod = paymentMethod,
            MinAmount = minAmount,
            MaxAmount = maxAmount,
            From = from,
            To = to,
            Search = search,
            RecordedByUserId = recordedByUserId,
            Payee = payee,
            IncludeVoided = !excludeVoided,
            Sort = sort,
            SortDirection = sortDirection,
            // An expense list is outgoing by definition; stating it keeps the direction axis
            // truthful even if a caller passes something else.
            Direction = "outgoing",
            Page = page,
            PageSize = pageSize
        }, userId.Value, role!);

        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>Record money the business spent.</summary>
    [HttpPost("expenses")]
    [Authorize(Policy = MembershipPolicies.OwnerOrStaff)]
    [RequirePermission("finances.manage_expenses")]
    [ProducesResponseType(typeof(ApiResponse<FinancialTransactionResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> CreateExpense([FromBody] CreateExpenseRequest request)
    {
        var (userId, role) = GetIdentity();
        if (userId == null) return Unauthorized();

        var result = await _expenses.CreateExpenseAsync(request, userId.Value, role!);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>Edit an expense. The change is audited; a reconciled expense cannot be edited.</summary>
    [HttpPut("expenses/{id:guid}")]
    [Authorize(Policy = MembershipPolicies.OwnerOrStaff)]
    [RequirePermission("finances.manage_expenses")]
    [ProducesResponseType(typeof(ApiResponse<FinancialTransactionResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateExpense(Guid id, [FromBody] UpdateExpenseRequest request)
    {
        var (userId, role) = GetIdentity();
        if (userId == null) return Unauthorized();

        var result = await _expenses.UpdateExpenseAsync(id, request, userId.Value, role!);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>
    /// Void an expense: it stays visible, stops counting, and the reason is recorded. Financial records
    /// are never deleted.
    /// </summary>
    [HttpPost("expenses/{id:guid}/void")]
    [Authorize(Policy = MembershipPolicies.OwnerOrStaff)]
    [RequirePermission("finances.manage_expenses")]
    [ProducesResponseType(typeof(ApiResponse<FinancialTransactionResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> VoidExpense(Guid id, [FromBody] VoidFinancialRecordRequest? request)
    {
        var (userId, role) = GetIdentity();
        if (userId == null) return Unauthorized();

        var result = await _expenses.VoidExpenseAsync(id, request ?? new VoidFinancialRecordRequest(),
            userId.Value, role!);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    // ═══════════════════════════════════════════════════════════
    //  M-PESA RECONCILIATION
    // ═══════════════════════════════════════════════════════════

    /// <summary>The reconciliation workspace: the latest import, its lines and both sides' loose ends.</summary>
    [HttpGet("reconciliation")]
    [Authorize(Policy = MembershipPolicies.OwnerOrStaff)]
    [RequirePermission("finances.reconcile")]
    [ProducesResponseType(typeof(ApiResponse<ReconciliationWorkspaceResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Reconciliation()
    {
        var (userId, role) = GetIdentity();
        if (userId == null) return Unauthorized();

        var result = await _reconciliation.GetWorkspaceAsync(userId.Value, role!);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>Import an M-Pesa statement (CSV) and match it against the ledger.</summary>
    [HttpPost("reconciliation/import")]
    [Authorize(Policy = MembershipPolicies.OwnerOrStaff)]
    [RequirePermission("finances.reconcile")]
    [ProducesResponseType(typeof(ApiResponse<ReconciliationWorkspaceResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ImportStatement([FromBody] ImportMpesaStatementRequest request)
    {
        var (userId, role) = GetIdentity();
        if (userId == null) return Unauthorized();

        var result = await _reconciliation.ImportStatementAsync(request, userId.Value, role!);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>Manually match (or exclude) one imported statement line.</summary>
    [HttpPost("reconciliation/lines/{lineId:guid}/match")]
    [Authorize(Policy = MembershipPolicies.OwnerOrStaff)]
    [RequirePermission("finances.reconcile")]
    [ProducesResponseType(typeof(ApiResponse<MpesaStatementLineResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> MatchStatementLine(Guid lineId, [FromBody] MatchStatementLineRequest request)
    {
        var (userId, role) = GetIdentity();
        if (userId == null) return Unauthorized();

        var result = await _reconciliation.MatchLineAsync(lineId, request, userId.Value, role!);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    // ═══════════════════════════════════════════════════════════
    //  M-PESA PAYMENT CLAIMS (the reconciliation board)
    // ═══════════════════════════════════════════════════════════
    //
    // Reconciliation is where a business owner receives the M-PESA confirmation a
    // customer pasted, checks it against their own M-PESA account, and decides whether
    // the money arrived. It is the same domain service that backs /v1/payments/verification
    // (IPaymentService) — these routes are the Finance module's door onto it, gated by
    // the `financials` module and the owner-only `finances.*` permissions rather than by
    // the `payments` module. No second verification mechanism exists: both doors call the
    // same idempotent, transactional methods.

    /// <summary>The claim queue: pending first, with per-status counts for the tabs.</summary>
    [HttpGet("reconciliation/claims")]
    [Authorize(Policy = MembershipPolicies.OwnerOrStaff)]
    [RequirePermission("finances.reconcile")]
    [ProducesResponseType(typeof(ApiResponse<PaymentVerificationQueueResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Claims(
        [FromQuery] string? status, [FromQuery] string? search,
        [FromQuery] DateTime? from, [FromQuery] DateTime? to,
        [FromQuery] DateTime? transactionFrom, [FromQuery] DateTime? transactionTo,
        [FromQuery] decimal? minAmount, [FromQuery] decimal? maxAmount,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25)
    {
        var (userId, role) = GetIdentity();
        if (userId == null) return Unauthorized();

        var result = await _payments.GetVerificationQueueAsync(new PaymentVerificationQuery
        {
            Status = status, Search = search, From = from, To = to,
            TransactionFrom = transactionFrom, TransactionTo = transactionTo,
            MinAmount = minAmount, MaxAmount = maxAmount, Page = page, PageSize = pageSize
        }, userId.Value, role!);

        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>One claim in full, for the verification workspace.</summary>
    [HttpGet("reconciliation/claims/{paymentId:guid}")]
    [Authorize(Policy = MembershipPolicies.OwnerOrStaff)]
    [RequirePermission("finances.reconcile")]
    [ProducesResponseType(typeof(ApiResponse<PaymentVerificationItemResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Claim(Guid paymentId)
    {
        var (userId, role) = GetIdentity();
        if (userId == null) return Unauthorized();

        var result = await _payments.GetVerificationItemAsync(paymentId, userId.Value, role!);
        return result.Success ? Ok(result) : MapFailure(result);
    }


    /// <summary>
    /// Confirm the money arrived. The only path from a pasted message to a settled
    /// payment: it settles the claim, recognises the income in the ledger exactly once,
    /// and leaves the appointment's own lifecycle status alone unless it was still
    /// waiting on money.
    /// </summary>
    [HttpPost("reconciliation/claims/{paymentId:guid}/verify")]
    [Authorize(Policy = MembershipPolicies.OwnerOrStaff)]
    [RequirePermission("finances.verify")]
    [ProducesResponseType(typeof(ApiResponse<PaymentResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> VerifyClaim(Guid paymentId, [FromBody] VerifyPaymentRequest request)
    {
        var (userId, role) = GetIdentity();
        if (userId == null) return Unauthorized();

        var result = await _payments.VerifyPaymentAsync(paymentId, request ?? new VerifyPaymentRequest(), userId.Value, role!);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>Refuse a claim. The reason is mandatory and is shown to the customer.</summary>
    [HttpPost("reconciliation/claims/{paymentId:guid}/reject")]
    [Authorize(Policy = MembershipPolicies.OwnerOrStaff)]
    [RequirePermission("finances.verify")]
    [ProducesResponseType(typeof(ApiResponse<PaymentResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RejectClaim(Guid paymentId, [FromBody] RejectPaymentRequest request)
    {
        var (userId, role) = GetIdentity();
        if (userId == null) return Unauthorized();

        var result = await _payments.RejectPaymentAsync(paymentId, request ?? new RejectPaymentRequest(), userId.Value, role!);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>Set a claim aside for a second look. Non-terminal; the note stays internal.</summary>
    [HttpPost("reconciliation/claims/{paymentId:guid}/flag")]
    [Authorize(Policy = MembershipPolicies.OwnerOrStaff)]
    [RequirePermission("finances.verify")]
    [ProducesResponseType(typeof(ApiResponse<PaymentResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> FlagClaim(Guid paymentId, [FromBody] FlagPaymentRequest request)
    {
        var (userId, role) = GetIdentity();
        if (userId == null) return Unauthorized();

        var result = await _payments.FlagPaymentAsync(paymentId, request ?? new FlagPaymentRequest(), userId.Value, role!);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    /// <summary>Clear a flag and return the claim to an ordinary open review.</summary>
    [HttpPost("reconciliation/claims/{paymentId:guid}/under-review")]
    [Authorize(Policy = MembershipPolicies.OwnerOrStaff)]
    [RequirePermission("finances.verify")]
    [ProducesResponseType(typeof(ApiResponse<PaymentResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ReviewClaim(Guid paymentId)
    {
        var (userId, role) = GetIdentity();
        if (userId == null) return Unauthorized();

        // A flagged claim is cleared back to an ordinary review first; an already-open
        // review is a no-op. Both are non-terminal, so no money decision is implied.
        var result = await _payments.MarkVerificationUnderReviewAsync(paymentId, userId.Value, role!);
        return result.Success ? Ok(result) : MapFailure(result);
    }

    // ═══════════════════════════════════════════════════════════
    //  SHARED
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// The status code a failure deserves. Conflicts are states the caller must understand (already
    /// verified, reconciled, or a refund larger than what is left), not malformed requests.
    /// </summary>
    private IActionResult MapFailure<T>(ApiResponse<T> result)
        => result.Error?.Code switch
        {
            "NOT_FOUND" => NotFound(result),
            "FORBIDDEN" => StatusCode(StatusCodes.Status403Forbidden, result),
            "DUPLICATE_REFERENCE" or "REFUND_EXCEEDS_AVAILABLE" or "INVALID_STATUS_TRANSITION"
                or "MPESA_CODE_ALREADY_USED" or "DUPLICATE_PAYMENT" or "IDEMPOTENCY_CONFLICT" => Conflict(result),
            _ => BadRequest(result)
        };

    private (Guid? userId, string? role) GetIdentity()
    {
        var role = User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value
                   ?? User.FindFirst("role")?.Value;
        var claim = User.FindFirst("userId")?.Value;
        return Guid.TryParse(claim, out var id) ? (id, role) : (null, role);
    }
}
