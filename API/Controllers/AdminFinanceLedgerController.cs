using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PunchedApi.Application.Authorization;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;

namespace PunchedApi.API.Controllers;

/// <summary>
/// Platform-operator view of the payment → ledger outbox: what is outstanding, what failed, why, and
/// how to put it back in the queue.
///
/// <para><b>Why this is admin-only.</b> An obligation is internal accounting plumbing; a business
/// owner has no business retrying it by hand, and exposing failure internals would leak infrastructure
/// details into a customer-facing product. The business-facing signal is the coarse
/// <c>ledgerProcessingStatus</c> on the payment itself. These endpoints sit behind
/// <see cref="MembershipPolicies.PlatformAdmin"/>, never a <c>finances.*</c> permission, so nothing
/// here can be reached with a tenant's own role.</para>
/// </summary>
[ApiController]
[Route("v1/admin/finance/ledger")]
[Produces("application/json")]
[Authorize(Policy = MembershipPolicies.PlatformAdmin)]
[EnableRateLimiting("general")]
public class AdminFinanceLedgerController : ControllerBase
{
    private readonly IPaymentLedgerOutbox _outbox;
    private readonly IPaymentLedgerProcessor _processor;
    private readonly IPaymentLedgerReconciliationService _reconciliation;
    private readonly ILogger<AdminFinanceLedgerController> _logger;

    public AdminFinanceLedgerController(
        IPaymentLedgerOutbox outbox,
        IPaymentLedgerProcessor processor,
        IPaymentLedgerReconciliationService reconciliation,
        ILogger<AdminFinanceLedgerController> logger)
    {
        _outbox = outbox;
        _processor = processor;
        _reconciliation = reconciliation;
        _logger = logger;
    }

    /// <summary>Ledger consistency: outstanding work, failures and detected anomalies.</summary>
    [HttpGet("health")]
    [ProducesResponseType(typeof(ApiResponse<AdminLedgerHealthResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Health(
        [FromQuery] Guid? businessId = null, [FromQuery] int samples = 5, CancellationToken cancellationToken = default)
    {
        var report = await _reconciliation.GetConsistencyReportAsync(businessId, samples, cancellationToken);

        return Ok(ApiResponse<AdminLedgerHealthResponse>.Ok(new AdminLedgerHealthResponse
        {
            BusinessId = businessId,
            Summary = ToSummary(report.Summary),
            SettledPaymentsWithoutIncome = report.SettledPaymentsWithoutIncome,
            LedgerRowsForUnsettledPayments = report.LedgerRowsForUnsettledPayments,
            DuplicateIncomeGroups = report.DuplicateIncomeGroups,
            OrphanedIncomeRows = report.OrphanedIncomeRows,
            Samples = report.Samples.Select(s => new AdminLedgerAnomalyResponse
            {
                Kind = s.Kind,
                PaymentId = s.PaymentId,
                Reference = s.Reference,
                Detail = s.Detail
            }).ToList(),
            GeneratedAt = DateTime.UtcNow
        }));
    }

    /// <summary>Paged obligations, newest problems first. Filter by business or status.</summary>
    [HttpGet("obligations")]
    [ProducesResponseType(typeof(ApiResponse<AdminLedgerObligationListResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Obligations(
        [FromQuery] Guid? businessId = null,
        [FromQuery] string? status = null,
        [FromQuery] bool problemsOnly = false,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        var filter = new PaymentLedgerFilter(businessId, status, problemsOnly, Math.Max(1, page), pageSize);

        var items = await _outbox.ListAsync(filter, cancellationToken);
        var total = await _outbox.CountAsync(filter, cancellationToken);

        return Ok(ApiResponse<AdminLedgerObligationListResponse>.Ok(new AdminLedgerObligationListResponse
        {
            Items = items.Select(ToView).ToList(),
            Page = filter.Page,
            PageSize = Math.Clamp(filter.PageSize, 1, 200),
            TotalCount = total
        }));
    }

    /// <summary>One obligation, including its attempt history and last failure.</summary>
    [HttpGet("obligations/{id:guid}")]
    [ProducesResponseType(typeof(ApiResponse<AdminLedgerObligationResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Obligation(Guid id, CancellationToken cancellationToken)
    {
        var view = await _outbox.GetViewAsync(id, cancellationToken);
        if (view == null)
            return NotFound(ApiResponse<AdminLedgerObligationResponse>.Fail("NOT_FOUND", "Obligation not found."));

        return Ok(ApiResponse<AdminLedgerObligationResponse>.Ok(ToView(view)));
    }

    /// <summary>
    /// Puts a failed obligation back in the queue and attempts it immediately. Idempotent in effect:
    /// if the income already exists, the obligation is completed rather than duplicated.
    /// </summary>
    [HttpPost("obligations/{id:guid}/retry")]
    [ProducesResponseType(typeof(ApiResponse<AdminLedgerRetryResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Retry(Guid id, CancellationToken cancellationToken)
    {
        var existing = await _outbox.GetViewAsync(id, cancellationToken);
        if (existing == null)
            return NotFound(ApiResponse<AdminLedgerRetryResponse>.Fail("NOT_FOUND", "Obligation not found."));

        var requeued = await _outbox.RequeueAsync(id, null, cancellationToken);

        LedgerProcessResult? result = null;
        if (requeued)
            result = await _processor.ProcessAsync(existing.PaymentId, cancellationToken);

        _logger.LogWarning(
            "Operator retried payment-ledger obligation {ObligationId} (payment {PaymentId}, business {BusinessId}); requeued={Requeued}, outcome={Outcome}.",
            id, existing.PaymentId, existing.BusinessId, requeued, result?.Outcome.ToString() ?? "none");

        var refreshed = await _outbox.GetViewAsync(id, cancellationToken);

        return Ok(ApiResponse<AdminLedgerRetryResponse>.Ok(new AdminLedgerRetryResponse
        {
            Requeued = requeued,
            Outcome = result?.Outcome.ToString() ?? (requeued ? "Queued" : "NotRequeued"),
            FinancialTransactionId = result?.FinancialTransactionId,
            ErrorCode = result?.ErrorCode,
            Obligation = refreshed == null ? null : ToView(refreshed)
        }));
    }

    /// <summary>Runs one bounded reconciliation pass (repairs missing obligations, completes stale ones).</summary>
    [HttpPost("reconcile")]
    [ProducesResponseType(typeof(ApiResponse<AdminLedgerReconciliationResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Reconcile([FromQuery] int batchSize = 100, CancellationToken cancellationToken = default)
    {
        var result = await _reconciliation.ReconcileAsync(batchSize, cancellationToken);

        _logger.LogInformation(
            "Operator-triggered ledger reconciliation: created={Created}, completed={Completed}, missing={Missing}.",
            result.ObligationsCreated, result.ObligationsCompleted, result.SettledPaymentsWithoutIncome);

        return Ok(ApiResponse<AdminLedgerReconciliationResponse>.Ok(new AdminLedgerReconciliationResponse
        {
            ObligationsCreated = result.ObligationsCreated,
            ObligationsCompleted = result.ObligationsCompleted,
            ObligationsRequeued = result.ObligationsRequeued,
            SettledPaymentsWithoutIncome = result.SettledPaymentsWithoutIncome,
            DuplicateIncomeGroups = result.DuplicateIncomeGroups,
            OrphanedIncomeRows = result.OrphanedIncomeRows,
            FailedObligations = result.FailedObligations,
            StartedAt = result.StartedAt,
            FinishedAt = result.FinishedAt
        }));
    }

    private static AdminLedgerSummaryResponse ToSummary(PaymentLedgerSummary summary) => new()
    {
        Pending = summary.Pending,
        Processing = summary.Processing,
        Failed = summary.Failed,
        Completed = summary.Completed,
        SettledPaymentsMissingIncome = summary.SettledPaymentsMissingIncome,
        ObligationsWithoutIncome = summary.ObligationsWithoutIncome,
        OldestPendingAgeMinutes = summary.OldestPendingAgeMinutes
    };

    private static AdminLedgerObligationResponse ToView(PaymentLedgerObligationView view) => new()
    {
        Id = view.Id,
        PaymentId = view.PaymentId,
        BusinessId = view.BusinessId,
        PaymentReference = view.PaymentReference,
        PaymentAmount = view.PaymentAmount,
        PaymentStatus = view.PaymentStatus,
        Status = view.Status,
        Attempts = view.Attempts,
        CreatedAt = view.CreatedAt,
        LastAttemptAt = view.LastAttemptAt,
        NextAttemptAt = view.NextAttemptAt,
        CompletedAt = view.CompletedAt,
        LockedAt = view.LockedAt,
        FinancialTransactionId = view.FinancialTransactionId,
        FailureKind = view.FailureKind,
        LastErrorCode = view.LastErrorCode,
        LastError = view.LastError,
        AgeMinutes = view.AgeMinutes
    };
}

