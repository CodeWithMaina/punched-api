using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

/// <summary>
/// Business income, the transaction list, decisions on recorded claims, refunds and
/// reconciliation — every operation that writes to or reads from the business ledger.
///
/// <para><b>What this service refuses to do.</b> It never edits the amount on a verified income
/// row (a settled figure is history), never deletes a row (audit needs it), and never treats a
/// recorded claim as confirmed revenue. Mistakes are voided or reversed, never erased.</para>
/// </summary>
public partial class FinancialService : IFinancialService
{
    private readonly ApplicationDbContext _context;
    private readonly IUnitOfWork _uow;
    private readonly ILogger<FinancialService> _logger;
    private readonly IIdempotencyService? _idempotency;

    public FinancialService(
        ApplicationDbContext context,
        IUnitOfWork uow,
        ILogger<FinancialService> logger,
        IIdempotencyService? idempotency = null)
    {
        _context = context;
        _uow = uow;
        _logger = logger;
        // Optional so unit tests (and any caller that does not need retry protection) can build
        // the service without it; the refund path degrades to "no replay" rather than failing.
        _idempotency = idempotency;
    }

    /// <summary>Punched ledger reference: FIN- + 12 hex characters, unique across the platform.</summary>
    internal static string GenerateReference()
    {
        Span<byte> bytes = stackalloc byte[6];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return "FIN-" + Convert.ToHexString(bytes);
    }

    /// <summary>Appends an audit entry for a ledger row (own save; a failure never fails the write).</summary>
    internal async Task WriteAuditAsync(
        Guid businessId, string entityType, Guid entityId, string action,
        Guid? actorUserId, string? reference, string? note, string? changesJson)
        => await FinancialAudit.WriteAsync(
            _context, _uow, _logger, businessId, entityType, entityId, action, actorUserId, reference, note, changesJson);

    /// <summary>Serialises before/after values for the audit trail.</summary>
    internal static string Changes(object? before, object? after) =>
        JsonSerializer.Serialize(new { before, after });

    // ═══════════════════════════════════════════════════════════
    //  STANDALONE INCOME
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// Records business income that did not come from a Punched appointment. No appointment and
    /// no customer are required: a walk-in who paid cash is a complete transaction.
    /// </summary>
    public async Task<ApiResponse<FinancialTransactionResponse>> RecordIncomeAsync(
        RecordIncomeRequest request, Guid userId, string role)
    {
        var businessId = await FinancialAccess.ResolveBusinessIdAsync(_context, userId);
        if (businessId is not Guid business)
            return ApiResponse<FinancialTransactionResponse>.Fail("FORBIDDEN", "No business scope for user.");

        if (request.Amount <= 0)
            return ApiResponse<FinancialTransactionResponse>.Fail(
                "VALIDATION_FAILED", "Amount must be greater than zero.");

        if (request.Amount > 100_000_000m)
            return ApiResponse<FinancialTransactionResponse>.Fail("VALIDATION_FAILED", "Amount is out of range.");

        if (!FinancialWire.TryParseSource(request.Source, out var source))
            return ApiResponse<FinancialTransactionResponse>.Fail(
                "VALIDATION_FAILED", "Income source is required (walk_in, product_sale, manual_income or other).");

        // Booking income is recognised from the payment lifecycle, never typed in by hand:
        // accepting it here would let a business invent appointment revenue no payment backs.
        if (source == TransactionSource.PunchedBooking)
            return ApiResponse<FinancialTransactionResponse>.Fail(
                "VALIDATION_FAILED",
                "Booking income is recorded automatically when a payment is settled; choose walk_in, product_sale, manual_income or other.");

        if (!FinancialWire.TryParseMethod(request.PaymentMethod, out var method))
            return ApiResponse<FinancialTransactionResponse>.Fail(
                "VALIDATION_FAILED",
                "Payment method is required (mpesa_till, mpesa_paybill, mpesa_send_money, cash, bank_transfer, card or other).");

        var reference = string.IsNullOrWhiteSpace(request.TransactionReference)
            ? null
            : FinancialLedgerService.Truncate(request.TransactionReference!.Trim(), 100);

        // An M-Pesa claim with no receipt code cannot be checked against anything, and being able
        // to check it later is the whole point of recording it.
        if (FinancialWire.IsMpesa(method) && reference == null)
            return ApiResponse<FinancialTransactionResponse>.Fail(
                "VALIDATION_FAILED", "An M-Pesa transaction reference (the receipt code) is required for M-Pesa income.");

        string? customerName = null;
        if (request.CustomerId is Guid customerId)
        {
            // The customer must belong to THIS business: a caller cannot attach another tenant's
            // customer to its books.
            if (!await FinancialAccess.CustomerBelongsToAsync(_context, customerId, business))
                return ApiResponse<FinancialTransactionResponse>.Fail("NOT_FOUND", "Customer not found.");

            customerName = await _context.Users.AsNoTracking()
                .Where(u => u.Id == customerId)
                .Select(u => u.FullName)
                .FirstOrDefaultAsync();
        }

        // The unique SourcePaymentId index cannot help here (there is no payment), so a reused
        // receipt code is checked explicitly.
        if (reference != null)
        {
            var duplicate = await _context.FinancialTransactions.AsNoTracking()
                .Where(t => t.BusinessId == business
                            && t.ExternalReference == reference
                            && t.Type == FinancialTransactionType.Income
                            && t.VerificationStatus != FinancialVerificationStatus.Rejected)
                .Select(t => t.Reference)
                .FirstOrDefaultAsync();

            if (duplicate != null)
                return ApiResponse<FinancialTransactionResponse>.Fail(
                    "DUPLICATE_REFERENCE",
                    $"That transaction reference has already been recorded as {duplicate}.");
        }

        // Owner-recorded evidence is a claim, not proof: an M-Pesa row waits for a decision. Cash,
        // bank and card rows are confirmed by the act of recording, because the person who had the
        // money in hand is the person entering it.
        var verification = FinancialWire.IsMpesa(method)
            ? FinancialVerificationStatus.Pending
            : FinancialVerificationStatus.Verified;

        var transaction = new FinancialTransaction
        {
            Id = Guid.NewGuid(),
            BusinessId = business,
            Type = FinancialTransactionType.Income,
            Source = source,
            PaymentMethod = method,
            VerificationStatus = verification,
            ReconciliationStatus = ReconciliationStatus.Unreconciled,
            Amount = request.Amount,
            Currency = string.IsNullOrWhiteSpace(request.Currency) ? "KES" : request.Currency!.Trim().ToUpperInvariant(),
            Reference = GenerateReference(),
            ExternalReference = reference,
            Category = ExpenseCategories.Normalise(request.Category),
            Description = FinancialLedgerService.Truncate(request.Description, 500),
            CustomerId = request.CustomerId,
            CustomerName = customerName,
            TransactionDate = request.TransactionDate?.ToUniversalTime() ?? DateTime.UtcNow,
            RecordedAt = DateTime.UtcNow,
            VerifiedAt = verification == FinancialVerificationStatus.Verified ? DateTime.UtcNow : null,
            VerifiedByUserId = verification == FinancialVerificationStatus.Verified ? userId : null,
            CreatedByUserId = userId,
            UpdatedAt = DateTime.UtcNow
        };

        await _context.FinancialTransactions.AddAsync(transaction);
        await _uow.SaveChangesAsync();

        await WriteAuditAsync(business, "transaction", transaction.Id, "created", userId, transaction.Reference,
            $"Recorded {transaction.Currency} {transaction.Amount:0.00} ({FinancialWire.ToWire(source)} / {FinancialWire.ToWire(method)}).",
            null);

        _logger.LogInformation(
            "Standalone income {Reference} recorded by user {UserId} for business {BusinessId}: {Amount} {Currency} via {Method} ({Verification}).",
            transaction.Reference, userId, business, transaction.Amount, transaction.Currency,
            transaction.PaymentMethod, transaction.VerificationStatus);

        return ApiResponse<FinancialTransactionResponse>.Ok(FinancialMapper.ToResponse(transaction));
    }
}
