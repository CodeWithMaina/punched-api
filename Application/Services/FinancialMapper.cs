using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Application.Services;

/// <summary>
/// Mapping from ledger rows to their API shape, kept in one place so the list, the detail
/// view, the overview's activity feed and the reconciliation fallbacks all agree.
/// </summary>
internal static class FinancialMapper
{
    public static FinancialTransactionResponse ToResponse(
        FinancialTransaction t, decimal refundedAmount = 0m, string? createdByName = null) => new()
        {
            Id = t.Id,
            BusinessId = t.BusinessId,
            Type = FinancialWire.ToWire(t.Type),
            Source = FinancialWire.ToWire(t.Source),
            PaymentMethod = FinancialWire.ToWire(t.PaymentMethod),
            VerificationStatus = FinancialWire.ToWire(t.VerificationStatus),
            ReconciliationStatus = FinancialWire.ToWire(t.ReconciliationStatus),
            Amount = t.Amount,
            SignedAmount = SignedAmount(t),
            Direction = FinancialWire.ToWire(FinancialWire.DirectionOf(t.Type)),
            Currency = t.Currency,
            Reference = t.Reference,
            ExternalReference = t.ExternalReference,
            Category = t.Category,
            CategoryLabel = t.Category == null ? null : ExpenseCategories.Label(t.Category),
            Description = t.Description,
            CustomerId = t.CustomerId,
            CustomerName = t.CustomerName,
            AppointmentId = t.AppointmentId,
            SourcePaymentId = t.SourcePaymentId,
            RefundOfTransactionId = t.RefundOfTransactionId,
            RefundReason = t.RefundReason,
            RefundReference = t.RefundReference,
            TransactionDate = t.TransactionDate,
            RecordedAt = t.RecordedAt,
            VerifiedAt = t.VerifiedAt,
            VerifiedByUserId = t.VerifiedByUserId,
            RejectionReason = t.RejectionReason,
            CreatedByUserId = t.CreatedByUserId,
            UpdatedAt = t.UpdatedAt,
            IsVoided = t.IsVoided,
            VoidedAt = t.VoidedAt,
            VoidReason = t.VoidReason,
            ReconciledAt = t.ReconciledAt,
            StatementLineId = t.StatementLineId,
            ReconciliationNote = t.ReconciliationNote,
            RefundableAmount = RefundableAmount(t, refundedAmount),
            // "Owner-recorded" means the owner typed the evidence rather than a provider confirming
            // it — exactly the distinction the screen must make honest.
            IsOwnerRecorded = t.SourcePaymentId == null,
            CreatedByName = createdByName
        };

    /// <summary>Income is positive; expenses and refunds reduce the net. Voided rows move nothing.</summary>
    public static decimal SignedAmount(FinancialTransaction t) =>
        t.IsVoided ? 0m : t.Type == FinancialTransactionType.Income || t.Type == FinancialTransactionType.Adjustment
            ? t.Amount
            : -t.Amount;

    /// <summary>
    /// How much of a verified income row is still refundable: its own amount minus the refunds
    /// already recorded against it. A voided, pending or non-income row is never refundable.
    /// </summary>
    public static decimal RefundableAmount(FinancialTransaction t, decimal refundedAmount)
    {
        if (!IsRefundableSource(t)) return 0m;
        var remaining = t.Amount - refundedAmount;
        return remaining > 0m ? remaining : 0m;
    }

    public static bool IsRefundableSource(FinancialTransaction t) =>
        !t.IsVoided
        && t.Type == FinancialTransactionType.Income
        && t.VerificationStatus == FinancialVerificationStatus.Verified;

    /// <summary>Income contributions to a total (voided rows never count).</summary>
    public static bool IsRecognisedIncome(FinancialTransaction t) =>
        !t.IsVoided
        && t.Type == FinancialTransactionType.Income
        && t.VerificationStatus == FinancialVerificationStatus.Verified;

    public static bool IsExpense(FinancialTransaction t) =>
        !t.IsVoided && t.Type == FinancialTransactionType.Expense;

    public static bool IsRefund(FinancialTransaction t) =>
        !t.IsVoided && t.Type == FinancialTransactionType.Refund;
}
