using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Application.Services;

/// <summary>
/// Reading the ledger, deciding on recorded claims, refunding and reconciling. Split from the
/// income path so each file keeps to one job.
///
/// <para>Every method here resolves the caller's business from their membership and filters on
/// it, so a request carrying somebody else's id simply finds nothing.</para>
/// </summary>
public partial class FinancialService
{
    /// <summary>One ledger row, tenant-scoped.</summary>
    public async Task<ApiResponse<FinancialTransactionResponse>> GetTransactionAsync(
        Guid id, Guid userId, string role)
    {
        var businessId = await FinancialAccess.ResolveBusinessIdAsync(_context, userId);
        if (businessId is not Guid business)
            return ApiResponse<FinancialTransactionResponse>.Fail("FORBIDDEN", "No business scope for user.");

        var transaction = await _context.FinancialTransactions.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == id && t.BusinessId == business);

        if (transaction == null)
            return ApiResponse<FinancialTransactionResponse>.Fail("NOT_FOUND", "Transaction not found.");

        var refunded = await SumRefundsAsync(business, new[] { transaction.Id });
        var names = await FinancialAccess.ResolveUserNamesAsync(_context, new[] { transaction.CreatedByUserId });

        return ApiResponse<FinancialTransactionResponse>.Ok(
            FinancialMapper.ToResponse(
                transaction,
                refunded.GetValueOrDefault(transaction.Id),
                transaction.CreatedByUserId is Guid recorder ? names.GetValueOrDefault(recorder) : null));
    }

    /// <summary>Filtered, sorted, server-paginated ledger rows plus the totals for that filter.</summary>
    public async Task<ApiResponse<FinancialTransactionListResponse>> ListTransactionsAsync(
        FinancialTransactionQuery query, Guid userId, string role)
    {
        var businessId = await FinancialAccess.ResolveBusinessIdAsync(_context, userId);
        if (businessId is not Guid business)
            return ApiResponse<FinancialTransactionListResponse>.Fail("FORBIDDEN", "No business scope for user.");

        var filtered = FinancialQueries.ApplyFilters(
            _context, _context.FinancialTransactions.AsNoTracking(), query, business);

        var (page, pageSize, skip) = FinancialQueries.Paging(query.Page, query.PageSize);

        var total = await filtered.CountAsync();

        // The totals describe the whole filtered set, not the visible page: a "Total income" caption
        // that changed when the owner turned the page would be worse than no caption. They are summed
        // in memory from a narrow projection so every provider reports the same figure, and so
        // filtering and totalling read exactly the same rows.
        var totalsRows = await filtered
            .Select(t => new
            {
                t.Amount,
                t.Type,
                t.VerificationStatus,
                t.IsVoided,
                t.ExternalReference
            })
            .ToListAsync();

        var verifiedIncomeTotal = totalsRows
            .Where(t => !t.IsVoided
                        && t.Type == FinancialTransactionType.Income
                        && t.VerificationStatus == FinancialVerificationStatus.Verified)
            .Sum(t => t.Amount);

        var expenseTotal = totalsRows
            .Where(t => !t.IsVoided && t.Type == FinancialTransactionType.Expense)
            .Sum(t => t.Amount);

        var refundTotal = totalsRows
            .Where(t => !t.IsVoided && t.Type == FinancialTransactionType.Refund)
            .Sum(t => t.Amount);

        var rows = await FinancialQueries.ApplySort(filtered, query.Sort, query.SortDirection)
            .Skip(skip)
            .Take(pageSize)
            .ToListAsync();

        var refunds = await SumRefundsAsync(business, rows.Select(r => r.Id).ToList());
        var names = await FinancialAccess.ResolveUserNamesAsync(_context, rows.Select(r => r.CreatedByUserId));

        return ApiResponse<FinancialTransactionListResponse>.Ok(new FinancialTransactionListResponse
        {
            Items = rows.Select(r => FinancialMapper.ToResponse(
                r,
                refunds.GetValueOrDefault(r.Id),
                r.CreatedByUserId is Guid recorder ? names.GetValueOrDefault(recorder) : null)).ToList(),
            Page = page,
            PageSize = pageSize,
            TotalCount = total,
            VerifiedIncomeTotal = verifiedIncomeTotal,
            ExpenseTotal = expenseTotal,
            RefundTotal = refundTotal,
            TotalPages = pageSize <= 0 ? 1 : (int)Math.Ceiling(total / (double)pageSize)
        });
    }

    /// <summary>
    /// How much has already been refunded against each of the given income rows, in one query.
    /// </summary>
    private async Task<Dictionary<Guid, decimal>> SumRefundsAsync(Guid businessId, IReadOnlyCollection<Guid> transactionIds)
    {
        if (transactionIds.Count == 0) return new Dictionary<Guid, decimal>();

        var ids = transactionIds.ToList();

        var refunds = await _context.FinancialTransactions.AsNoTracking()
            .Where(t => t.BusinessId == businessId
                        && t.Type == FinancialTransactionType.Refund
                        && !t.IsVoided
                        && t.RefundOfTransactionId != null
                        && ids.Contains(t.RefundOfTransactionId!.Value))
            .Select(t => new RefundOfRow(t.RefundOfTransactionId!.Value, t.Amount))
            .ToListAsync();

        return refunds
            .GroupBy(r => r.RefundOf)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.Amount));
    }

    // ═══════════════════════════════════════════════════════════
    //  DECISIONS ON RECORDED CLAIMS
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// Confirms a recorded claim. This is the only path from "the owner typed this in" to
    /// "the business holds this money", and it is deliberately a person's decision.
    /// </summary>
    public async Task<ApiResponse<FinancialTransactionResponse>> VerifyTransactionAsync(
        Guid id, VerifyFinancialTransactionRequest request, Guid userId, string role)
    {
        var businessId = await FinancialAccess.ResolveBusinessIdAsync(_context, userId);
        if (businessId is not Guid business)
            return ApiResponse<FinancialTransactionResponse>.Fail("FORBIDDEN", "No business scope for user.");

        var transaction = await _context.FinancialTransactions
            .FirstOrDefaultAsync(t => t.Id == id && t.BusinessId == business);

        if (transaction == null)
            return ApiResponse<FinancialTransactionResponse>.Fail("NOT_FOUND", "Transaction not found.");

        if (transaction.IsVoided)
            return ApiResponse<FinancialTransactionResponse>.Fail(
                "INVALID_STATUS_TRANSITION", "A voided transaction cannot be verified.");

        if (transaction.Type != FinancialTransactionType.Income)
            return ApiResponse<FinancialTransactionResponse>.Fail(
                "INVALID_STATUS_TRANSITION", "Only income is verified; expenses are recorded or voided.");

        // Already decided: same answer, no side effects repeated.
        if (transaction.VerificationStatus == FinancialVerificationStatus.Verified)
            return ApiResponse<FinancialTransactionResponse>.Ok(FinancialMapper.ToResponse(transaction));

        var before = new { transaction.Amount, Status = FinancialWire.ToWire(transaction.VerificationStatus) };

        if (request.Amount is decimal corrected && corrected > 0)
        {
            // A corrected figure is allowed exactly once, here, at the moment of the decision — and
            // it is captured in the audit trail, so the claim and the confirmed amount are both
            // recoverable afterwards.
            transaction.Amount = corrected;
        }

        transaction.VerificationStatus = FinancialVerificationStatus.Verified;
        transaction.VerifiedAt = DateTime.UtcNow;
        transaction.VerifiedByUserId = userId;
        transaction.RejectionReason = null;
        transaction.UpdatedAt = DateTime.UtcNow;

        await _uow.SaveChangesAsync();

        await WriteAuditAsync(business, "transaction", transaction.Id, "verified", userId, transaction.Reference,
            FinancialLedgerService.Truncate(
                request.Note ?? $"Verified {transaction.Currency} {transaction.Amount:0.00}.", 500),
            Changes(before, new { transaction.Amount, Status = "verified" }));

        _logger.LogInformation(
            "Ledger transaction {Reference} VERIFIED by user {UserId} for business {BusinessId} ({Amount} {Currency}).",
            transaction.Reference, userId, business, transaction.Amount, transaction.Currency);

        var refunded = await SumRefundsAsync(business, new[] { transaction.Id });

        return ApiResponse<FinancialTransactionResponse>.Ok(
            FinancialMapper.ToResponse(transaction, refunded.GetValueOrDefault(transaction.Id)));
    }

    /// <summary>Refuses a recorded claim. The reason is required: the trail must say why.</summary>
    public async Task<ApiResponse<FinancialTransactionResponse>> RejectTransactionAsync(
        Guid id, RejectFinancialTransactionRequest request, Guid userId, string role)
    {
        var businessId = await FinancialAccess.ResolveBusinessIdAsync(_context, userId);
        if (businessId is not Guid business)
            return ApiResponse<FinancialTransactionResponse>.Fail("FORBIDDEN", "No business scope for user.");

        if (string.IsNullOrWhiteSpace(request.Reason))
            return ApiResponse<FinancialTransactionResponse>.Fail(
                "VALIDATION_FAILED", "A reason is required when rejecting a recorded transaction.");

        var transaction = await _context.FinancialTransactions
            .FirstOrDefaultAsync(t => t.Id == id && t.BusinessId == business);

        if (transaction == null)
            return ApiResponse<FinancialTransactionResponse>.Fail("NOT_FOUND", "Transaction not found.");

        if (transaction.IsVoided)
            return ApiResponse<FinancialTransactionResponse>.Fail(
                "INVALID_STATUS_TRANSITION", "A voided transaction cannot be rejected.");

        if (transaction.VerificationStatus == FinancialVerificationStatus.Verified)
            return ApiResponse<FinancialTransactionResponse>.Fail(
                "INVALID_STATUS_TRANSITION",
                "This transaction has already been verified. Void it instead — history is not rewritten.");

        transaction.VerificationStatus = FinancialVerificationStatus.Rejected;
        transaction.RejectionReason = FinancialLedgerService.Truncate(request.Reason!.Trim(), 500);
        transaction.UpdatedAt = DateTime.UtcNow;

        await _uow.SaveChangesAsync();

        await WriteAuditAsync(business, "transaction", transaction.Id, "rejected", userId, transaction.Reference,
            transaction.RejectionReason,
            Changes(null, new { Status = "rejected" }));

        _logger.LogWarning(
            "Ledger transaction {Reference} REJECTED by user {UserId} for business {BusinessId}: {Reason}",
            transaction.Reference, userId, business, transaction.RejectionReason);

        return ApiResponse<FinancialTransactionResponse>.Ok(FinancialMapper.ToResponse(transaction));
    }

    // ═══════════════════════════════════════════════════════════
    //  REFUNDS
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// Records a refund against an income row. The original row is never touched: the refund is a
    /// new, linked, independently provable record, and it can be full or partial.
    /// </summary>
    public async Task<ApiResponse<FinancialTransactionResponse>> RecordRefundAsync(
        RecordRefundRequest request, Guid userId, string role)
    {
        var businessId = await FinancialAccess.ResolveBusinessIdAsync(_context, userId);
        if (businessId is not Guid business)
            return ApiResponse<FinancialTransactionResponse>.Fail("FORBIDDEN", "No business scope for user.");

        if (request.Amount <= 0)
            return ApiResponse<FinancialTransactionResponse>.Fail(
                "VALIDATION_FAILED", "Refund amount must be greater than zero.");

        if (string.IsNullOrWhiteSpace(request.Reason))
            return ApiResponse<FinancialTransactionResponse>.Fail(
                "VALIDATION_FAILED", "A refund reason is required.");

        if (!FinancialWire.TryParseMethod(request.RefundMethod ?? "cash", out var refundMethod))
            return ApiResponse<FinancialTransactionResponse>.Fail(
                "VALIDATION_FAILED", "Refund method is not recognised.");

        // Retry-safe. A client that re-sends because it never saw the response must not cause the
        // customer to be refunded twice, so a repeated key returns the FIRST refund rather than
        // recording another one.
        var idempotencyKey = string.IsNullOrWhiteSpace(request.IdempotencyKey)
            ? null
            : request.IdempotencyKey!.Trim();

        var requestHash = RefundRequestHash(request);

        if (idempotencyKey != null && _idempotency != null)
        {
            var lookup = await _idempotency.TryGetAsync(idempotencyKey, userId, requestHash);

            if (lookup.Conflict)
                return ApiResponse<FinancialTransactionResponse>.Fail(
                    "IDEMPOTENCY_CONFLICT",
                    "That idempotency key was already used for a different refund.");

            if (lookup is { Found: true, ResponseJson: not null })
            {
                var replayed = JsonSerializer.Deserialize<FinancialTransactionResponse>(lookup.ResponseJson);
                if (replayed != null)
                    return ApiResponse<FinancialTransactionResponse>.Ok(replayed);
            }
        }

        // Serializable, and held across the locate → cap-check → insert, so two refunds racing each
        // other cannot both read "KES 2,000 still refundable" and both take it. One of them wins
        // and the other is retried against the new balance (or is replayed by the key above).
        await using var transaction = await _context.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable);

        // Locate the income being refunded: either by ledger id or by the payment it came from.
        FinancialTransaction? income = null;

        if (request.TransactionId is Guid transactionId)
        {
            income = await _context.FinancialTransactions
                .FirstOrDefaultAsync(t => t.Id == transactionId && t.BusinessId == business);
        }
        else if (request.PaymentId is Guid paymentId)
        {
            income = await _context.FinancialTransactions
                .FirstOrDefaultAsync(t => t.SourcePaymentId == paymentId && t.BusinessId == business);
        }

        if (income == null)
            return ApiResponse<FinancialTransactionResponse>.Fail(
                "NOT_FOUND", "The income being refunded was not found in this business's books.");

        if (!FinancialMapper.IsRefundableSource(income))
            return ApiResponse<FinancialTransactionResponse>.Fail(
                "INVALID_STATUS_TRANSITION",
                income.IsVoided
                    ? "A voided transaction cannot be refunded."
                    : "Only verified income can be refunded.");

        var refunded = await SumRefundsAsync(business, new[] { income.Id });
        var alreadyRefunded = refunded.GetValueOrDefault(income.Id);
        var refundable = income.Amount - alreadyRefunded;

        // Refunding more than came in would invent money that was never received.
        if (request.Amount > refundable)
            return ApiResponse<FinancialTransactionResponse>.Fail(
                "REFUND_EXCEEDS_AVAILABLE",
                $"Only {income.Currency} {refundable:0.00} of {income.Reference} is left to refund" +
                (alreadyRefunded > 0 ? $" ({income.Currency} {alreadyRefunded:0.00} already refunded)." : "."));

        var refund = new FinancialTransaction
        {
            Id = Guid.NewGuid(),
            BusinessId = business,
            Type = FinancialTransactionType.Refund,
            Source = income.Source,
            PaymentMethod = refundMethod,
            // A requested refund is not a completed one: the money has left only when the business
            // says it has. Until then it shows as pending and never reduces verified income.
            VerificationStatus = request.Completed
                ? FinancialVerificationStatus.Verified
                : FinancialVerificationStatus.Pending,
            ReconciliationStatus = ReconciliationStatus.Unreconciled,
            Amount = request.Amount,
            Currency = income.Currency,
            Reference = GenerateReference(),
            ExternalReference = FinancialLedgerService.Truncate(request.RefundReference, 100),
            Description = FinancialLedgerService.Truncate($"Refund of {income.Reference}", 500),
            RefundReason = FinancialLedgerService.Truncate(request.Reason!.Trim(), 500),
            RefundReference = FinancialLedgerService.Truncate(request.RefundReference, 100),
            CustomerId = income.CustomerId,
            CustomerName = income.CustomerName,
            AppointmentId = income.AppointmentId,
            RefundOfTransactionId = income.Id,
            TransactionDate = request.RefundDate?.ToUniversalTime() ?? DateTime.UtcNow,
            RecordedAt = DateTime.UtcNow,
            VerifiedAt = request.Completed ? DateTime.UtcNow : null,
            VerifiedByUserId = request.Completed ? userId : null,
            CreatedByUserId = userId,
            UpdatedAt = DateTime.UtcNow
        };

        await _context.FinancialTransactions.AddAsync(refund);
        await _uow.SaveChangesAsync();

        await WriteAuditAsync(business, "refund", refund.Id, "refunded", userId, income.Reference,
            $"Refund of {refund.Currency} {refund.Amount:0.00} against {income.Reference}{(request.Completed ? " (completed)" : " (requested)")}.",
            Changes(null, new { income.Reference, refund.Amount, Completed = request.Completed }));

        var response = FinancialMapper.ToResponse(refund);

        await transaction.CommitAsync();

        // Armed only AFTER the money is committed, so a replay can only ever return a refund that
        // really exists in the books.
        if (idempotencyKey != null && _idempotency != null)
        {
            await _idempotency.StoreAsync(
                idempotencyKey, userId, requestHash, JsonSerializer.Serialize(response));
        }

        _logger.LogWarning(
            "Refund {Reference} of {Amount} {Currency} recorded against {Income} by user {UserId} (business {BusinessId}, completed: {Completed}).",
            refund.Reference, refund.Amount, refund.Currency, income.Reference, userId, business, request.Completed);

        return ApiResponse<FinancialTransactionResponse>.Ok(response);
    }

    /// <summary>
    /// A stable hash of the fields that make one refund request distinct: the same key with the same
    /// body replays, the same key with a different body is a conflict the caller must resolve.
    /// </summary>
    private static string RefundRequestHash(RecordRefundRequest request) =>
        string.Join('|',
            request.TransactionId?.ToString() ?? "-",
            request.PaymentId?.ToString() ?? "-",
            request.Amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            request.RefundMethod ?? "-",
            request.RefundReference ?? "-",
            request.Completed ? "completed" : "requested");

    // ═══════════════════════════════════════════════════════════
    //  RECONCILIATION
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// Records the owner's reconciliation verdict on a transaction. Independent of verification:
    /// this says "I compared it with my statement", not "the money arrived".
    /// </summary>
    public async Task<ApiResponse<FinancialTransactionResponse>> ReconcileTransactionAsync(
        Guid id, ReconcileTransactionRequest request, Guid userId, string role)
    {
        var businessId = await FinancialAccess.ResolveBusinessIdAsync(_context, userId);
        if (businessId is not Guid business)
            return ApiResponse<FinancialTransactionResponse>.Fail("FORBIDDEN", "No business scope for user.");

        if (!FinancialWire.TryParseReconciliation(request.Status, out var status))
            return ApiResponse<FinancialTransactionResponse>.Fail(
                "VALIDATION_FAILED", "Status must be matched, discrepancy, excluded or unreconciled.");

        var transaction = await _context.FinancialTransactions
            .FirstOrDefaultAsync(t => t.Id == id && t.BusinessId == business);

        if (transaction == null)
            return ApiResponse<FinancialTransactionResponse>.Fail("NOT_FOUND", "Transaction not found.");

        var before = FinancialWire.ToWire(transaction.ReconciliationStatus);

        Guid? statementLineId = null;

        if (request.StatementLineId is Guid lineId)
        {
            // The line must belong to this business, so a reconciliation can never be written
            // against another tenant's imported statement.
            var line = await _context.MpesaStatementLines.AsNoTracking()
                .FirstOrDefaultAsync(l => l.Id == lineId && l.BusinessId == business);

            if (line == null)
                return ApiResponse<FinancialTransactionResponse>.Fail("NOT_FOUND", "Statement line not found.");

            statementLineId = line.Id;
        }

        transaction.ReconciliationStatus = status;
        transaction.StatementLineId = status == ReconciliationStatus.Matched ? statementLineId : null;
        transaction.ReconciliationNote = FinancialLedgerService.Truncate(request.Note, 500);
        transaction.ReconciledAt = status == ReconciliationStatus.Unreconciled ? null : DateTime.UtcNow;
        transaction.ReconciledByUserId = status == ReconciliationStatus.Unreconciled ? null : userId;
        transaction.UpdatedAt = DateTime.UtcNow;

        await _uow.SaveChangesAsync();

        await WriteAuditAsync(business, "transaction", transaction.Id, "reconciled", userId, transaction.Reference,
            transaction.ReconciliationNote ?? $"Reconciliation set to {FinancialWire.ToWire(status)}.",
            Changes(new { Status = before }, new { Status = FinancialWire.ToWire(status) }));

        return ApiResponse<FinancialTransactionResponse>.Ok(FinancialMapper.ToResponse(transaction));
    }

    // ═══════════════════════════════════════════════════════════
    //  AUDIT TRAIL
    // ═══════════════════════════════════════════════════════════

    /// <summary>A record's audit trail, newest first.</summary>
    public async Task<ApiResponse<List<FinancialAuditLogResponse>>> GetAuditTrailAsync(
        Guid id, Guid userId, string role)
    {
        var businessId = await FinancialAccess.ResolveBusinessIdAsync(_context, userId);
        if (businessId is not Guid business)
            return ApiResponse<List<FinancialAuditLogResponse>>.Fail("FORBIDDEN", "No business scope for user.");

        var exists = await _context.FinancialTransactions.AsNoTracking()
            .AnyAsync(t => t.Id == id && t.BusinessId == business);

        if (!exists)
            return ApiResponse<List<FinancialAuditLogResponse>>.Fail("NOT_FOUND", "Transaction not found.");

        var entries = await _context.FinancialAuditLogs.AsNoTracking()
            .Where(l => l.BusinessId == business && l.EntityId == id)
            .OrderByDescending(l => l.CreatedAt)
            .Take(200)
            .Select(l => new FinancialAuditLogResponse
            {
                Id = l.Id,
                EntityType = l.EntityType,
                EntityId = l.EntityId,
                Action = l.Action,
                ActorUserId = l.ActorUserId,
                ActorName = l.ActorName,
                Note = l.Note,
                ChangesJson = l.ChangesJson,
                Reference = l.Reference,
                CreatedAt = l.CreatedAt
            })
            .ToListAsync();

        return ApiResponse<List<FinancialAuditLogResponse>>.Ok(entries);
    }
}

