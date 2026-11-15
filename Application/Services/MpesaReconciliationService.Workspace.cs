using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Application.Services;

/// <summary>The reconciliation workspace: the latest import, its lines, and both sides' loose ends.</summary>
public partial class MpesaReconciliationService
{
    private async Task<ReconciliationWorkspaceResponse> BuildWorkspaceAsync(Guid businessId)
    {
        var imports = await _context.MpesaStatementImports.AsNoTracking()
            .Where(i => i.BusinessId == businessId)
            .OrderByDescending(i => i.ImportedAt)
            .Take(20)
            .ToListAsync();

        var latest = imports.FirstOrDefault();

        var response = new ReconciliationWorkspaceResponse
        {
            Import = latest == null ? null : ToImportResponse(latest),
            Imports = imports.Select(ToImportResponse).ToList()
        };

        response.UnreconciledTransactionCount = await _context.FinancialTransactions.AsNoTracking()
            .CountAsync(t => t.BusinessId == businessId
                             && t.ReconciliationStatus == ReconciliationStatus.Unreconciled
                             && !t.IsVoided);

        if (latest == null) return response;

        var lines = await _context.MpesaStatementLines.AsNoTracking()
            .Where(l => l.ImportId == latest.Id)
            .OrderByDescending(l => l.TransactionAt ?? l.CreatedAt)
            .Take(WorkspaceLineLimit)
            .ToListAsync();

        var matchedIds = lines
            .Where(l => l.MatchedTransactionId != null)
            .Select(l => l.MatchedTransactionId!.Value)
            .Distinct()
            .ToList();

        var matched = await _context.FinancialTransactions.AsNoTracking()
            .Where(t => matchedIds.Contains(t.Id))
            .Select(t => new LedgerReferenceRow(t.Id, t.Amount, t.Reference))
            .ToListAsync();

        var matchedById = matched.ToDictionary(t => t.Id, t => (t.Amount, t.Reference));

        // Receipts the business has imported (any batch), so "recorded here but missing from the
        // statement" needs no second import to answer.
        var statementReceipts = await _context.MpesaStatementLines.AsNoTracking()
            .Where(l => l.BusinessId == businessId && !l.IsDuplicateInFile)
            .Select(l => l.ReceiptNumber)
            .Distinct()
            .ToListAsync();

        var receiptSet = statementReceipts.ToHashSet(StringComparer.OrdinalIgnoreCase);

        response.Lines = lines.Select(line => ToLineResponse(
            line,
            line.MatchedTransactionId.HasValue
                ? matchedById.GetValueOrDefault(line.MatchedTransactionId.Value)
                : default)).ToList();

        response.LineTotal = await _context.MpesaStatementLines.AsNoTracking()
            .CountAsync(l => l.ImportId == latest.Id);

        response.MatchedLines = lines.Count(l => l.Status == ReconciliationStatus.Matched);
        response.UnmatchedLines = lines.Count(l => l.Status == ReconciliationStatus.Unreconciled);
        response.DiscrepancyLines = lines.Count(l => l.Status == ReconciliationStatus.Discrepancy);

        // Money in Punched's books with no statement evidence. Listed for the business to judge —
        // never silently re-classified, because a statement may cover a different period.
        var missing = await _context.FinancialTransactions.AsNoTracking()
            .Where(t => t.BusinessId == businessId
                        && !t.IsVoided
                        && t.Type == FinancialTransactionType.Income
                        && t.VerificationStatus == FinancialVerificationStatus.Verified
                        && t.ReconciliationStatus != ReconciliationStatus.Excluded
                        && (t.ExternalReference == null || !receiptSet.Contains(t.ExternalReference)))
            .OrderByDescending(t => t.TransactionDate)
            .Take(WorkspaceLineLimit)
            .Select(t => new UnreconciledTransactionResponse
            {
                Id = t.Id,
                Reference = t.Reference,
                ExternalReference = t.ExternalReference,
                Amount = t.Amount,
                PaymentMethod = FinancialWire.ToWire(t.PaymentMethod),
                VerificationStatus = FinancialWire.ToWire(t.VerificationStatus),
                TransactionDate = t.TransactionDate,
                Description = t.Description,
                CustomerName = t.CustomerName
            })
            .ToListAsync();

        response.TransactionsMissingFromStatement = missing;
        response.TransactionsMissingFromStatementCount = missing.Count;

        return response;
    }

    private static MpesaStatementLineResponse ToLineResponse(
        MpesaStatementLine line, (decimal Amount, string Reference) matched)
    {
        var hasMatch = matched.Reference != null;

        return new MpesaStatementLineResponse
        {
            Id = line.Id,
            ImportId = line.ImportId,
            ReceiptNumber = line.ReceiptNumber,
            Amount = line.Amount,
            Currency = line.Currency,
            TransactionAt = line.TransactionAt,
            PayerName = line.PayerName,
            PayerPhone = line.PayerPhone,
            AccountReference = line.AccountReference,
            Narrative = line.Narrative,
            Status = FinancialWire.ToWire(line.Status),
            MatchedTransactionId = line.MatchedTransactionId,
            MatchedPaymentId = line.MatchedPaymentId,
            MatchMethod = line.MatchMethod,
            Note = line.Note,
            IsDuplicateInFile = line.IsDuplicateInFile,
            PunchedAmount = hasMatch ? matched.Amount : null,
            PunchedReference = hasMatch ? matched.Reference : null,
            AmountVariance = hasMatch ? line.Amount - matched.Amount : null
        };
    }
}
