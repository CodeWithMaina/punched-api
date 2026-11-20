using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

/// <summary>
/// Recognises settled payments in the business ledger, and nothing else.
///
/// <para><b>Why this is the only coupling between the two modules.</b> The payment module owns
/// collection (created → submitted → verified/rejected, or a provider callback); the ledger
/// owns recognition. Rather than duplicating payment rows, a settled payment produces ONE
/// linked ledger row keyed by <see cref="FinancialTransaction.SourcePaymentId"/> — the unique
/// index that makes "counted twice" impossible even when a callback is replayed or an owner
/// confirms twice.</para>
///
/// <para><b>Pending money is not revenue.</b> Only Success/Verified payments are recognised.
/// Anything still awaiting the customer or the owner stays visible as a pending total, computed
/// from the payments themselves — a row in the ledger would imply it had been earned.</para>
/// </summary>
public class FinancialLedgerService : IFinancialLedgerService
{
    private readonly ApplicationDbContext _context;
    private readonly IUnitOfWork _uow;
    private readonly ILogger<FinancialLedgerService> _logger;

    public FinancialLedgerService(
        ApplicationDbContext context,
        IUnitOfWork uow,
        ILogger<FinancialLedgerService> logger)
    {
        _context = context;
        _uow = uow;
        _logger = logger;
    }

    public async Task<FinancialTransaction?> EnsureIncomeForPaymentAsync(
        Payment payment, CancellationToken cancellationToken = default)
    {
        // Kept for every existing caller: the money has already moved, so this path never throws.
        var result = await TryEnsureIncomeForPaymentAsync(payment, cancellationToken);
        return result.Transaction;
    }

    /// <summary>
    /// The same recognition, but reporting <i>why</i> it did not produce a row.
    ///
    /// <para>The payment module needs that distinction: a transient database failure must be
    /// retried by the outbox worker, while an invalid amount must stop and stay visible for a human.
    /// Swallowing both into <c>null</c> — as the old method did — is precisely how a settled payment
    /// could end up with no income and no signal.</para>
    /// </summary>
    public async Task<LedgerIncomeResult> TryEnsureIncomeForPaymentAsync(
        Payment payment, CancellationToken cancellationToken = default)
    {
        if (!PaymentStatuses.IsSettled(payment.Status))
            return LedgerIncomeResult.PermanentFailure(
                "PAYMENT_NOT_SETTLED",
                "Only a settled payment can be recognised as income.");

        // Financial data is validated here, before a row is built: a zero/negative amount or a
        // missing currency is a data defect, not an outage, and retrying it forever would hide it.
        if (payment.Amount <= 0)
            return LedgerIncomeResult.PermanentFailure(
                "INVALID_AMOUNT",
                "The settled amount is not greater than zero, so it cannot become income.");

        if (string.IsNullOrWhiteSpace(payment.Currency) || payment.Currency.Trim().Length != 3)
            return LedgerIncomeResult.PermanentFailure(
                "INVALID_CURRENCY",
                "The payment does not carry a valid ISO currency.");

        // Already recognised: a replayed callback or a repeated confirmation is a no-op.
        var existing = await _context.FinancialTransactions
            .FirstOrDefaultAsync(t => t.SourcePaymentId == payment.Id, cancellationToken);

        if (existing != null)
            return LedgerIncomeResult.Existing(existing);

        var appointmentContext = await BuildAppointmentContextAsync(payment, cancellationToken);
        var method = await ResolvePaymentMethodAsync(payment, cancellationToken);

        var decidedAt = payment.CompletedAt ?? payment.VerifiedAt ?? payment.ParsedTransactionAt ?? DateTime.UtcNow;

        var transaction = new FinancialTransaction
        {
            Id = Guid.NewGuid(),
            BusinessId = payment.BusinessId,
            Type = FinancialTransactionType.Income,
            Source = TransactionSource.PunchedBooking,
            PaymentMethod = method,
            // Settled money is verified by definition: either the provider confirmed it, a
            // member of staff confirmed the cash, or the owner verified the pasted message.
            VerificationStatus = FinancialVerificationStatus.Verified,
            // Verification is NOT reconciliation: the row is unreconciled until it is compared
            // with an imported M-Pesa statement.
            ReconciliationStatus = ReconciliationStatus.Unreconciled,
            Amount = payment.Amount,
            Currency = payment.Currency,
            Reference = GenerateReference(),
            ExternalReference = payment.ExternalReference ?? payment.ParsedTransactionCode,
            Description = DescribePayment(payment, appointmentContext.ServiceNames),
            CustomerId = payment.CustomerId,
            CustomerName = appointmentContext.CustomerName,
            AppointmentId = payment.AppointmentId,
            SourcePaymentId = payment.Id,
            TransactionDate = decidedAt,
            RecordedAt = DateTime.UtcNow,
            VerifiedAt = payment.VerifiedAt ?? payment.CompletedAt,
            VerifiedByUserId = payment.VerifiedByUserId ?? payment.ConfirmedByUserId,
            CreatedByUserId = payment.VerifiedByUserId ?? payment.ConfirmedByUserId ?? payment.CreatedByUserId,
            UpdatedAt = DateTime.UtcNow
        };

        await _context.FinancialTransactions.AddAsync(transaction, cancellationToken);

        try
        {
            await _uow.SaveChangesAsync();

            await WriteAuditAsync(
                payment.BusinessId,
                "transaction",
                transaction.Id,
                "created",
                transaction.CreatedByUserId,
                transaction.Reference,
                $"Recognised {payment.Currency} {payment.Amount:0.00} from settled payment {payment.Reference}.",
                null,
                cancellationToken);

            return LedgerIncomeResult.Created(transaction);
        }
        catch (DbUpdateException ex)
        {
            // Lost a race with a concurrent settlement of the same payment: the unique index did
            // its job. Return the row the winner wrote instead of failing the caller — and report it
            // as `AlreadyExisted`, so the obligation is completed rather than retried.
            _context.Entry(transaction).State = EntityState.Detached;

            var winner = await _context.FinancialTransactions
                .AsNoTracking()
                .FirstOrDefaultAsync(t => t.SourcePaymentId == payment.Id, cancellationToken);

            if (winner != null)
                return LedgerIncomeResult.Existing(winner);

            // The insert was refused but no winner is visible: this is a real write conflict, not a
            // duplicate. It is retryable — and it must be retried, or the payment never becomes income.
            _logger.LogError(ex,
                "Ledger insert for payment {PaymentId} (business {BusinessId}) failed without a visible winner; it will be retried.",
                payment.Id, payment.BusinessId);

            return LedgerIncomeResult.TransientFailure(
                "LEDGER_WRITE_CONFLICT",
                "The ledger insert conflicted with another writer and no income row is visible yet.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Infrastructure failures (connection loss, transient Npgsql errors, timeouts) must be
            // retried by the outbox, not hidden. The message is logged in full; the obligation keeps
            // only a bounded, non-sensitive summary.
            _context.Entry(transaction).State = EntityState.Detached;
            _logger.LogError(ex,
                "Could not write income for payment {PaymentId} (business {BusinessId}); it will be retried by the ledger outbox.",
                payment.Id, payment.BusinessId);

            return LedgerIncomeResult.TransientFailure(
                "LEDGER_WRITE_FAILED",
                "The ledger write failed for an infrastructure reason and will be retried.");
        }
    }

    public async Task<FinancialTransaction?> EnsureRefundForReversedPaymentAsync(
        Payment payment, string reason, CancellationToken cancellationToken = default)
    {
        if (payment.Status is not (PaymentStatus.Reversed or PaymentStatus.Refunded))
            return null;

        var income = await _context.FinancialTransactions
            .FirstOrDefaultAsync(t => t.SourcePaymentId == payment.Id, cancellationToken);

        if (income == null)
        {
            // Nothing was ever recognised for this payment, so there is nothing to refund.
            // Recorded rather than invented: a refund row with no income behind it would
            // overstate money out.
            _logger.LogWarning(
                "Reversal of payment {PaymentId} has no recognised income row; no refund was recorded in the ledger.",
                payment.Id);
            return null;
        }

        // One refund per reversed payment: the reversal is a single terminal event.
        var existing = await _context.FinancialTransactions
            .FirstOrDefaultAsync(t => t.RefundOfTransactionId == income.Id
                                      && t.Type == FinancialTransactionType.Refund, cancellationToken);

        if (existing != null)
            return existing;

        var refund = new FinancialTransaction
        {
            Id = Guid.NewGuid(),
            BusinessId = payment.BusinessId,
            Type = FinancialTransactionType.Refund,
            Source = income.Source,
            PaymentMethod = income.PaymentMethod,
            // A completed reversal is money that has actually gone back.
            VerificationStatus = FinancialVerificationStatus.Verified,
            ReconciliationStatus = ReconciliationStatus.Unreconciled,
            Amount = income.Amount,
            Currency = income.Currency,
            Reference = GenerateReference(),
            ExternalReference = payment.ExternalReference,
            Description = Truncate(
                $"Refund of {income.Reference}{(string.IsNullOrWhiteSpace(reason) ? string.Empty : " — " + reason.Trim())}",
                500),
            CustomerId = payment.CustomerId,
            CustomerName = income.CustomerName,
            AppointmentId = payment.AppointmentId,
            RefundOfTransactionId = income.Id,
            RefundReason = Truncate(reason, 500),
            TransactionDate = payment.ReversedAt ?? DateTime.UtcNow,
            RecordedAt = DateTime.UtcNow,
            VerifiedAt = payment.ReversedAt ?? DateTime.UtcNow,
            CreatedByUserId = payment.VerifiedByUserId,
            UpdatedAt = DateTime.UtcNow
        };

        await _context.FinancialTransactions.AddAsync(refund, cancellationToken);
        await _uow.SaveChangesAsync();

        await WriteAuditAsync(
            payment.BusinessId,
            "refund",
            refund.Id,
            "refunded",
            refund.CreatedByUserId,
            refund.Reference,
            $"Refund of {refund.Currency} {refund.Amount:0.00} recorded against {income.Reference}.",
            null,
            cancellationToken);

        return refund;
    }

    /// <summary>
    /// Append an audit entry in its own save. Called after the financial row it describes has
    /// committed, so a failure to audit can never undo a decision the business already took.
    /// </summary>
    internal async Task WriteAuditAsync(
        Guid businessId,
        string entityType,
        Guid entityId,
        string action,
        Guid? actorUserId,
        string? reference,
        string? note,
        string? changesJson,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _context.FinancialAuditLogs.AddAsync(new FinancialAuditLog
            {
                Id = Guid.NewGuid(),
                BusinessId = businessId,
                EntityType = entityType,
                EntityId = entityId,
                Action = action,
                ActorUserId = actorUserId,
                ActorName = await FinancialAccess.ActorNameAsync(_context, actorUserId),
                Reference = reference,
                Note = Truncate(note, 500),
                ChangesJson = changesJson,
                CreatedAt = DateTime.UtcNow
            }, cancellationToken);

            await _uow.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Financial audit entry {Action} for {EntityType} {EntityId} could not be written.",
                action, entityType, entityId);
        }
    }

    // ── Internals ───────────────────────────────────────────

    private sealed record AppointmentContext(List<string> ServiceNames, string? CustomerName);

    /// <summary>
    /// The appointment's service names and the customer's name, for a human-readable
    /// description. Bounded to two queries so recognising a payment never fans out per row.
    /// </summary>
    private async Task<AppointmentContext> BuildAppointmentContextAsync(
        Payment payment, CancellationToken cancellationToken)
    {
        var serviceNames = payment.AppointmentId is Guid appointmentId
            ? await _context.AppointmentResources.AsNoTracking()
                .Where(r => r.AppointmentId == appointmentId)
                .OrderBy(r => r.SortOrder)
                .Select(r => r.Name)
                .ToListAsync(cancellationToken)
            : new List<string>();

        var customerName = payment.CustomerId is Guid customerId
            ? await _context.Users.AsNoTracking()
                .Where(u => u.Id == customerId)
                .Select(u => u.FullName)
                .FirstOrDefaultAsync(cancellationToken)
            : null;

        return new AppointmentContext(serviceNames, customerName);
    }

    /// <summary>
    /// Maps a settled payment onto the wider ledger payment methods. M-Pesa rows keep the channel
    /// the money actually arrived on (Till, Paybill or Send Money) so the payment-method breakdown
    /// is a real answer rather than "M-Pesa".
    /// </summary>
    private async Task<FinancialPaymentMethod> ResolvePaymentMethodAsync(
        Payment payment, CancellationToken cancellationToken)
    {
        if (payment.Method == PaymentMethod.Cash)
            return FinancialPaymentMethod.Cash;

        var config = await _context.BusinessPaymentConfigs.AsNoTracking()
            .FirstOrDefaultAsync(c => c.BusinessId == payment.BusinessId, cancellationToken);

        var isTill = string.Equals(
            config?.MpesaAccountType, ManualMpesaMethods.Till, StringComparison.OrdinalIgnoreCase);

        if (payment.Provider == PaymentProviderKind.ManualMpesa)
        {
            return config?.ActiveManualMethod() switch
            {
                ManualMpesaMethods.Till => FinancialPaymentMethod.MpesaTill,
                ManualMpesaMethods.SendMoney => FinancialPaymentMethod.MpesaSendMoney,
                ManualMpesaMethods.Paybill => FinancialPaymentMethod.MpesaPaybill,
                _ => isTill ? FinancialPaymentMethod.MpesaTill : FinancialPaymentMethod.MpesaPaybill
            };
        }

        return isTill ? FinancialPaymentMethod.MpesaTill : FinancialPaymentMethod.MpesaPaybill;
    }

    private static string DescribePayment(Payment payment, IReadOnlyList<string> serviceNames)
    {
        var what = payment.Type switch
        {
            PaymentType.BookingFee => "Booking fee",
            PaymentType.BalancePayment => "Balance payment",
            _ => "Appointment payment"
        };

        var services = serviceNames.Count > 0 ? string.Join(", ", serviceNames) : null;

        return Truncate(services == null ? what : $"{what} — {services}", 500) ?? what;
    }

    /// <summary>Punched ledger reference: FIN- + 12 hex chars, unique across the platform.</summary>
    private static string GenerateReference()
    {
        Span<byte> bytes = stackalloc byte[6];
        RandomNumberGenerator.Fill(bytes);
        return "FIN-" + Convert.ToHexString(bytes);
    }

    internal static string? Truncate(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= max ? value : value[..max];
}
