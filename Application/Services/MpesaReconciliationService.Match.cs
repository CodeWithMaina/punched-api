using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Application.Services;

/// <summary>Manual reconciliation decisions.</summary>
public partial class MpesaReconciliationService
{
    /// <summary>
    /// The owner's manual verdict on one line — used when the automatic pass was too cautious (or too
    /// generous). The line and the transaction it points at move together, and the decision is audited.
    /// </summary>
    public async Task<ApiResponse<MpesaStatementLineResponse>> MatchLineAsync(
        Guid lineId, MatchStatementLineRequest request, Guid userId, string role)
    {
        var businessId = await FinancialAccess.ResolveBusinessIdAsync(_context, userId);
        if (businessId is not Guid business)
            return ApiResponse<MpesaStatementLineResponse>.Fail("FORBIDDEN", "No business scope for user.");

        var line = await _context.MpesaStatementLines
            .FirstOrDefaultAsync(l => l.Id == lineId && l.BusinessId == business);

        if (line == null)
            return ApiResponse<MpesaStatementLineResponse>.Fail("NOT_FOUND", "Statement line not found.");

        if (!FinancialWire.TryParseReconciliation(request.Status, out var status) ||
            status == ReconciliationStatus.Unreconciled)
            return ApiResponse<MpesaStatementLineResponse>.Fail(
                "VALIDATION_FAILED", "Status must be matched, discrepancy or excluded.");

        FinancialTransaction? transaction = null;

        if (request.TransactionId is Guid transactionId)
        {
            transaction = await _context.FinancialTransactions
                .FirstOrDefaultAsync(t => t.Id == transactionId && t.BusinessId == business);

            if (transaction == null)
                return ApiResponse<MpesaStatementLineResponse>.Fail("NOT_FOUND", "Transaction not found.");
        }
        else if (line.MatchedTransactionId is Guid existingId)
        {
            transaction = await _context.FinancialTransactions
                .FirstOrDefaultAsync(t => t.Id == existingId && t.BusinessId == business);
        }

        // Releasing a line releases whatever it had claimed, so a corrected match never leaves two rows
        // pointing at the same statement line.
        if (line.MatchedTransactionId is Guid previousId && previousId != transaction?.Id)
        {
            var previous = await _context.FinancialTransactions
                .FirstOrDefaultAsync(t => t.Id == previousId && t.BusinessId == business);

            if (previous != null)
            {
                previous.ReconciliationStatus = ReconciliationStatus.Unreconciled;
                previous.StatementLineId = null;
                previous.ReconciledAt = null;
                previous.ReconciliationNote = null;
                previous.UpdatedAt = DateTime.UtcNow;
            }

            line.MatchedTransactionId = null;
        }

        line.Status = status;
        line.MatchMethod = "manual";
        line.Note = FinancialLedgerService.Truncate(request.Note, 500);
        line.UpdatedAt = DateTime.UtcNow;

        if (status == ReconciliationStatus.Matched && transaction != null)
        {
            line.MatchedTransactionId = transaction.Id;

            // A manual match is still an amount comparison: pointing a line at a transaction whose
            // amount differs is a discrepancy, however deliberately it was paired.
            transaction.ReconciliationStatus = transaction.Amount == line.Amount
                ? ReconciliationStatus.Matched
                : ReconciliationStatus.Discrepancy;
            transaction.StatementLineId = line.Id;
            transaction.ReconciledAt = DateTime.UtcNow;
            transaction.ReconciledByUserId = userId;
            transaction.ReconciliationNote = line.Note ??
                $"Manually matched to statement line {line.ReceiptNumber}.";
            transaction.UpdatedAt = DateTime.UtcNow;
        }
        else if (transaction != null)
        {
            // Excluded or disputed: the transaction is told, so the books never claim a match the
            // reconciler has just rejected.
            transaction.ReconciliationStatus = status;
            transaction.StatementLineId = status == ReconciliationStatus.Excluded ? null : line.Id;
            transaction.ReconciledAt = DateTime.UtcNow;
            transaction.ReconciledByUserId = userId;
            transaction.ReconciliationNote = line.Note;
            transaction.UpdatedAt = DateTime.UtcNow;
        }

        await _uow.SaveChangesAsync();

        await RecountImportAsync(line.ImportId);

        await FinancialAudit.WriteAsync(
            _context, _uow, _logger, business, "reconciliation", line.Id, "reconciled", userId,
            transaction?.Reference,
            line.Note ?? $"Statement line {line.ReceiptNumber} set to {FinancialWire.ToWire(status)}.",
            FinancialService.Changes(null, new { Status = FinancialWire.ToWire(status) }));

        return ApiResponse<MpesaStatementLineResponse>.Ok(ToLineResponse(
            line,
            transaction == null ? default : (transaction.Amount, transaction.Reference)));
    }
}
