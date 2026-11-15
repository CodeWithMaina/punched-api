using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Application.Services;

/// <summary>Importing a statement and matching it against the ledger.</summary>
public partial class MpesaReconciliationService
{
    /// <summary>
    /// Parses and imports a statement, then matches every line against Punched in the same pass.
    ///
    /// <para><b>Idempotency by content.</b> The file's SHA-256 is unique per business, so the same
    /// statement cannot be imported twice and double every line. Re-uploading it answers with the
    /// existing batch rather than an error the owner cannot act on.</para>
    /// </summary>
    public async Task<ApiResponse<ReconciliationWorkspaceResponse>> ImportStatementAsync(
        ImportMpesaStatementRequest request, Guid userId, string role)
    {
        var businessId = await FinancialAccess.ResolveBusinessIdAsync(_context, userId);
        if (businessId is not Guid business)
            return ApiResponse<ReconciliationWorkspaceResponse>.Fail("FORBIDDEN", "No business scope for user.");

        if (string.IsNullOrWhiteSpace(request.Content))
            return ApiResponse<ReconciliationWorkspaceResponse>.Fail(
                "VALIDATION_FAILED",
                "The statement content is required (paste the CSV from your M-Pesa statement).");

        var parsed = MpesaStatementCsv.Parse(request.Content!);

        if (parsed.Lines.Count == 0)
            return ApiResponse<ReconciliationWorkspaceResponse>.Fail(
                "VALIDATION_FAILED", parsed.Notes ?? "No transaction lines could be read from that file.");

        var hash = Hash(request.Content!);

        var existing = await _context.MpesaStatementImports.AsNoTracking()
            .FirstOrDefaultAsync(i => i.BusinessId == business && i.ContentHash == hash);

        if (existing != null)
        {
            // Reported as a no-op with the batch that already holds it: importing the same file again
            // must never create a second set of lines.
            _logger.LogInformation(
                "Statement {FileName} was already imported as batch {ImportId} for business {BusinessId}; returning the existing import.",
                existing.FileName, existing.Id, business);

            return ApiResponse<ReconciliationWorkspaceResponse>.Ok(await BuildWorkspaceAsync(business));
        }

        var import = new MpesaStatementImport
        {
            Id = Guid.NewGuid(),
            BusinessId = business,
            FileName = string.IsNullOrWhiteSpace(request.FileName)
                ? "mpesa-statement.csv"
                : FinancialLedgerService.Truncate(request.FileName!.Trim(), 260)!,
            Format = "csv",
            ContentHash = hash,
            LineCount = parsed.Lines.Count,
            ImportedByUserId = userId,
            ImportedAt = DateTime.UtcNow,
            Notes = FinancialLedgerService.Truncate(parsed.Notes, 500),
            CreatedAt = DateTime.UtcNow
        };

        await _context.MpesaStatementImports.AddAsync(import);

        // Receipts seen earlier in THIS file: a repeat inside one statement is a finding, not a second
        // transaction, so it is imported but excluded from matching.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var linesToMatch = new List<MpesaStatementLine>();

        foreach (var parsedLine in parsed.Lines)
        {
            var isDuplicate = !seen.Add(parsedLine.ReceiptNumber);

            var line = new MpesaStatementLine
            {
                Id = Guid.NewGuid(),
                BusinessId = business,
                ImportId = import.Id,
                ReceiptNumber = parsedLine.ReceiptNumber,
                Amount = parsedLine.Amount,
                Currency = "KES",
                TransactionAt = parsedLine.TransactionAt,
                PayerName = parsedLine.PayerName,
                PayerPhone = parsedLine.PayerPhone,
                AccountReference = parsedLine.AccountReference,
                Narrative = parsedLine.Narrative,
                Status = isDuplicate ? ReconciliationStatus.Excluded : ReconciliationStatus.Unreconciled,
                IsDuplicateInFile = isDuplicate,
                Note = isDuplicate ? "This receipt appears more than once in the imported file." : null,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            await _context.MpesaStatementLines.AddAsync(line);
            linesToMatch.Add(line);
        }

        await _uow.SaveChangesAsync();

        await MatchImportedLinesAsync(business, linesToMatch);
        await RecountImportAsync(import.Id);

        await FinancialAudit.WriteAsync(_context, _uow, _logger, business, "reconciliation", import.Id, "imported",
            userId, null,
            $"Imported {import.FileName}: {import.LineCount} line(s), {import.MatchedCount} matched, " +
            $"{import.DiscrepancyCount} discrepanc(ies), {import.UnmatchedCount} unmatched.",
            null);

        _logger.LogInformation(
            "M-Pesa statement {FileName} imported for business {BusinessId} by user {UserId}: {Lines} line(s), {Matched} matched, {Discrepancies} discrepancies, {Unmatched} unmatched.",
            import.FileName, business, userId, import.LineCount, import.MatchedCount,
            import.DiscrepancyCount, import.UnmatchedCount);

        return ApiResponse<ReconciliationWorkspaceResponse>.Ok(await BuildWorkspaceAsync(business));
    }

    /// <summary>
    /// Matches imported lines to Punched, by receipt code first and then by amount + date.
    ///
    /// <para><b>What each outcome means.</b> A line whose receipt is a ledger row's external reference
    /// is that row's evidence: matching amounts make it <c>matched</c>, differing amounts make it a
    /// <c>discrepancy</c>. A receipt that matches a payment Punched never recognised is recorded against
    /// the payment (the line is explained, and nothing is verified here). A receipt with no counterpart
    /// at all stays <c>unreconciled</c>: money M-Pesa saw that Punched did not.</para>
    /// </summary>
    private async Task MatchImportedLinesAsync(Guid businessId, List<MpesaStatementLine> lines)
    {
        var candidates = lines.Where(l => !l.IsDuplicateInFile).ToList();
        if (candidates.Count == 0) return;

        var receipts = candidates.Select(l => l.ReceiptNumber).Distinct().ToList();

        var ledgerMatches = await _context.FinancialTransactions
            .Where(t => t.BusinessId == businessId
                        && !t.IsVoided
                        && t.ExternalReference != null
                        && receipts.Contains(t.ExternalReference))
            .ToListAsync();

        var byReceipt = ledgerMatches
            .GroupBy(t => t.ExternalReference!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(t => t.TransactionDate).First(),
                StringComparer.OrdinalIgnoreCase);

        // Payments holding the receipt that were never recognised in the ledger (still pending, or
        // rejected): the line is explained, and the payment's own lifecycle is left alone.
        var paymentMatches = await _context.Payments.AsNoTracking()
            .Where(p => p.BusinessId == businessId
                        && ((p.ParsedTransactionCode != null && receipts.Contains(p.ParsedTransactionCode))
                            || (p.ExternalReference != null && receipts.Contains(p.ExternalReference))))
            .Select(p => new PaymentReceiptRow(p.Id, p.ParsedTransactionCode ?? p.ExternalReference!, p.Amount))
            .ToListAsync();

        var paymentsByReceipt = paymentMatches
            .GroupBy(p => p.Receipt, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var line in candidates)
        {
            if (byReceipt.TryGetValue(line.ReceiptNumber, out var transaction))
            {
                var amountMatches = line.Amount == transaction.Amount;

                line.MatchedTransactionId = transaction.Id;
                line.MatchMethod = "reference";
                line.Status = amountMatches ? ReconciliationStatus.Matched : ReconciliationStatus.Discrepancy;
                line.Note = amountMatches
                    ? $"Matched {transaction.Reference} by receipt code."
                    : $"Amount differs: M-Pesa says {line.Amount:0.00}, Punched recorded {transaction.Amount:0.00} " +
                      $"({transaction.Reference}).";
                line.UpdatedAt = DateTime.UtcNow;

                // Reconciliation is recorded on the row as well, so the transactions page can tell the
                // owner which rows were checked — without touching verification.
                transaction.ReconciliationStatus = line.Status;
                transaction.StatementLineId = line.Id;
                transaction.ReconciledAt = DateTime.UtcNow;
                transaction.ReconciledByUserId = null;
                transaction.ReconciliationNote = FinancialLedgerService.Truncate(line.Note, 500);
                transaction.UpdatedAt = DateTime.UtcNow;

                continue;
            }

            if (paymentsByReceipt.TryGetValue(line.ReceiptNumber, out var payment))
            {
                var amountMatches = line.Amount == payment.Amount;

                line.MatchedPaymentId = payment.Id;
                line.MatchMethod = "reference";
                line.Status = amountMatches ? ReconciliationStatus.Matched : ReconciliationStatus.Discrepancy;
                line.Note = amountMatches
                    ? "Matched a Punched payment that is not yet recognised income; its verification is unchanged."
                    : $"Amount differs from the Punched payment ({line.Amount:0.00} vs {payment.Amount:0.00}).";
                line.UpdatedAt = DateTime.UtcNow;

                continue;
            }

            // Amount + date is the fallback, and it is deliberately conservative: exactly ONE ledger
            // row, on the same calendar day, for the same amount, and not already claimed by another
            // line. A looser rule would match the wrong customer and destroy trust in the report.
            var day = (line.TransactionAt ?? DateTime.UtcNow).Date;

            var amountCandidates = await _context.FinancialTransactions
                .Where(t => t.BusinessId == businessId
                            && !t.IsVoided
                            && t.Amount == line.Amount
                            && t.StatementLineId == null
                            && t.TransactionDate >= day
                            && t.TransactionDate < day.AddDays(1))
                .ToListAsync();

            if (amountCandidates.Count == 1)
            {
                var only = amountCandidates[0];

                line.MatchedTransactionId = only.Id;
                line.MatchMethod = "amount_date";
                line.Status = ReconciliationStatus.Matched;
                line.Note = $"Matched {only.Reference} by amount and transaction date.";
                line.UpdatedAt = DateTime.UtcNow;

                only.ReconciliationStatus = ReconciliationStatus.Matched;
                only.StatementLineId = line.Id;
                only.ReconciledAt = DateTime.UtcNow;
                only.ReconciliationNote = "Auto-matched by amount and transaction date at import.";
                only.UpdatedAt = DateTime.UtcNow;

                continue;
            }

            line.Status = ReconciliationStatus.Unreconciled;
            line.Note = "No Punched transaction matches this receipt: money M-Pesa recorded that Punched has not.";
            line.UpdatedAt = DateTime.UtcNow;
        }

        await _uow.SaveChangesAsync();
    }

    /// <summary>Refreshes an import batch's counters from its lines (the numbers the screen shows).</summary>
    private async Task RecountImportAsync(Guid importId)
    {
        var import = await _context.MpesaStatementImports.FirstOrDefaultAsync(i => i.Id == importId);
        if (import == null) return;

        var statuses = await _context.MpesaStatementLines.AsNoTracking()
            .Where(l => l.ImportId == importId)
            .Select(l => l.Status)
            .ToListAsync();

        import.LineCount = statuses.Count;
        import.MatchedCount = statuses.Count(s => s == ReconciliationStatus.Matched);
        import.DiscrepancyCount = statuses.Count(s => s == ReconciliationStatus.Discrepancy);
        import.UnmatchedCount = statuses.Count(s => s == ReconciliationStatus.Unreconciled);
        import.ExcludedCount = statuses.Count(s => s == ReconciliationStatus.Excluded);

        await _uow.SaveChangesAsync();
    }
}
