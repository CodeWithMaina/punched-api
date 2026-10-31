using System.Text.Json.Serialization;

namespace PunchedApi.Application.DTOs;

// ═══════════════════════════════════════════════════════════════
//  COMMISSIONS — CONFIGURATION
// ═══════════════════════════════════════════════════════════════

/// <summary>The business's commission configuration (one per business).</summary>
public class CommissionConfigResponse
{
    [JsonPropertyName("businessId")] public Guid BusinessId { get; set; }
    [JsonPropertyName("isEnabled")] public bool IsEnabled { get; set; }
    /// <summary><c>percentage</c> | <c>fixed</c>.</summary>
    [JsonPropertyName("calculationType")] public string CalculationType { get; set; } = "percentage";
    [JsonPropertyName("defaultPercentage")] public decimal? DefaultPercentage { get; set; }
    [JsonPropertyName("defaultFixedAmount")] public decimal? DefaultFixedAmount { get; set; }
    [JsonPropertyName("currency")] public string Currency { get; set; } = "KES";
    [JsonPropertyName("requireSettlement")] public bool RequireSettlement { get; set; }
    [JsonPropertyName("requireApproval")] public bool RequireApproval { get; set; }
    [JsonPropertyName("earnOnNoShow")] public bool EarnOnNoShow { get; set; }
    [JsonPropertyName("earnOnCancelled")] public bool EarnOnCancelled { get; set; }
    [JsonPropertyName("allowPartialSettlement")] public bool AllowPartialSettlement { get; set; }
    [JsonPropertyName("minimumSettlementRatio")] public decimal MinimumSettlementRatio { get; set; }
    [JsonPropertyName("requireStaffServiceEligibility")] public bool RequireStaffServiceEligibility { get; set; }
    [JsonPropertyName("roundingDecimals")] public int RoundingDecimals { get; set; }
    [JsonPropertyName("notes")] public string? Notes { get; set; }
    [JsonPropertyName("updatedAt")] public DateTime UpdatedAt { get; set; }
}

/// <summary>Upserts the business's commission configuration.</summary>
public class UpdateCommissionConfigRequest
{
    [JsonPropertyName("isEnabled")] public bool IsEnabled { get; set; }
    /// <summary><c>percentage</c> | <c>fixed</c> (case-insensitive).</summary>
    [JsonPropertyName("calculationType")] public string CalculationType { get; set; } = "percentage";
    [JsonPropertyName("defaultPercentage")] public decimal? DefaultPercentage { get; set; }
    [JsonPropertyName("defaultFixedAmount")] public decimal? DefaultFixedAmount { get; set; }
    [JsonPropertyName("currency")] public string? Currency { get; set; }
    [JsonPropertyName("requireSettlement")] public bool RequireSettlement { get; set; } = true;
    [JsonPropertyName("requireApproval")] public bool RequireApproval { get; set; }
    [JsonPropertyName("earnOnNoShow")] public bool EarnOnNoShow { get; set; }
    [JsonPropertyName("earnOnCancelled")] public bool EarnOnCancelled { get; set; }
    [JsonPropertyName("allowPartialSettlement")] public bool AllowPartialSettlement { get; set; } = true;
    [JsonPropertyName("minimumSettlementRatio")] public decimal MinimumSettlementRatio { get; set; }
    [JsonPropertyName("requireStaffServiceEligibility")] public bool RequireStaffServiceEligibility { get; set; } = true;
    [JsonPropertyName("roundingDecimals")] public int RoundingDecimals { get; set; } = 2;
    [JsonPropertyName("notes")] public string? Notes { get; set; }
}

/// <summary>A scoped override of the business default arrangement.</summary>
public class CommissionRateResponse
{
    [JsonPropertyName("id")] public Guid Id { get; set; }
    [JsonPropertyName("scope")] public string Scope { get; set; } = string.Empty;
    [JsonPropertyName("staffUserId")] public Guid? StaffUserId { get; set; }
    [JsonPropertyName("staffName")] public string? StaffName { get; set; }
    [JsonPropertyName("serviceId")] public Guid? ServiceId { get; set; }
    [JsonPropertyName("serviceName")] public string? ServiceName { get; set; }
    [JsonPropertyName("calculationType")] public string CalculationType { get; set; } = "percentage";
    [JsonPropertyName("percentage")] public decimal? Percentage { get; set; }
    [JsonPropertyName("fixedAmount")] public decimal? FixedAmount { get; set; }
    [JsonPropertyName("isActive")] public bool IsActive { get; set; }
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; }
}

/// <summary>Creates or updates a scoped commission override.</summary>
public class UpsertCommissionRateRequest
{
    /// <summary><c>staff</c> | <c>service</c> | <c>staff_service</c>.</summary>
    [JsonPropertyName("scope")] public string Scope { get; set; } = string.Empty;
    [JsonPropertyName("staffUserId")] public Guid? StaffUserId { get; set; }
    [JsonPropertyName("serviceId")] public Guid? ServiceId { get; set; }
    /// <summary><c>percentage</c> | <c>fixed</c>.</summary>
    [JsonPropertyName("calculationType")] public string CalculationType { get; set; } = "percentage";
    [JsonPropertyName("percentage")] public decimal? Percentage { get; set; }
    [JsonPropertyName("fixedAmount")] public decimal? FixedAmount { get; set; }
    [JsonPropertyName("isActive")] public bool IsActive { get; set; } = true;
}


// ═══════════════════════════════════════════════════════════════
//  COMMISSIONS — LEDGER
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// One immutable ledger entry. Carries the full calculation context so an
/// amount can always be reproduced after the configuration has changed.
/// </summary>
public class CommissionEntryResponse
{
    [JsonPropertyName("id")] public Guid Id { get; set; }
    [JsonPropertyName("businessId")] public Guid BusinessId { get; set; }
    [JsonPropertyName("staffUserId")] public Guid StaffUserId { get; set; }
    [JsonPropertyName("staffName")] public string? StaffName { get; set; }
    [JsonPropertyName("customerId")] public Guid? CustomerId { get; set; }
    [JsonPropertyName("customerName")] public string? CustomerName { get; set; }
    [JsonPropertyName("appointmentId")] public Guid? AppointmentId { get; set; }
    [JsonPropertyName("appointmentResourceId")] public Guid? AppointmentResourceId { get; set; }
    [JsonPropertyName("serviceId")] public Guid? ServiceId { get; set; }
    [JsonPropertyName("serviceName")] public string? ServiceName { get; set; }
    [JsonPropertyName("paymentId")] public Guid? PaymentId { get; set; }
    [JsonPropertyName("payoutId")] public Guid? PayoutId { get; set; }

    /// <summary><c>earning</c> | <c>adjustment</c> | <c>reversal</c>.</summary>
    [JsonPropertyName("entryType")] public string EntryType { get; set; } = string.Empty;
    /// <summary><c>appointment</c> | <c>manual</c>.</summary>
    [JsonPropertyName("sourceType")] public string SourceType { get; set; } = string.Empty;
    /// <summary><c>pending</c> | <c>approved</c> | <c>payable</c> | <c>paid</c> | <c>disputed</c> | <c>reversed</c> | <c>void</c>.</summary>
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;

    /// <summary>Which configuration level produced this entry: <c>business</c> | <c>staff</c> | <c>service</c> | <c>staff_service</c>.</summary>
    [JsonPropertyName("configScope")] public string ConfigScope { get; set; } = string.Empty;
    [JsonPropertyName("configId")] public Guid? ConfigId { get; set; }
    /// <summary>Immutable snapshot of the configuration applied at accrual time.</summary>
    [JsonPropertyName("configSnapshot")] public string ConfigSnapshotJson { get; set; } = "{}";

    [JsonPropertyName("calculationType")] public string CalculationType { get; set; } = string.Empty;
    [JsonPropertyName("ratePercent")] public decimal? RatePercent { get; set; }
    [JsonPropertyName("fixedAmount")] public decimal? FixedAmount { get; set; }
    [JsonPropertyName("baseAmount")] public decimal BaseAmount { get; set; }
    [JsonPropertyName("amount")] public decimal Amount { get; set; }
    [JsonPropertyName("currency")] public string Currency { get; set; } = "KES";

    [JsonPropertyName("reason")] public string? Reason { get; set; }
    [JsonPropertyName("adjustmentOfEntryId")] public Guid? AdjustmentOfEntryId { get; set; }
    [JsonPropertyName("reversesEntryId")] public Guid? ReversesEntryId { get; set; }
    [JsonPropertyName("reversedByEntryId")] public Guid? ReversedByEntryId { get; set; }
    [JsonPropertyName("dedupeKey")] public string DedupeKey { get; set; } = string.Empty;

    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; }
    [JsonPropertyName("updatedAt")] public DateTime UpdatedAt { get; set; }
    [JsonPropertyName("approvedAt")] public DateTime? ApprovedAt { get; set; }
    [JsonPropertyName("approvedByUserId")] public Guid? ApprovedByUserId { get; set; }
    [JsonPropertyName("payableAt")] public DateTime? PayableAt { get; set; }
    [JsonPropertyName("paidAt")] public DateTime? PaidAt { get; set; }
    [JsonPropertyName("disputedAt")] public DateTime? DisputedAt { get; set; }
    [JsonPropertyName("disputeReason")] public string? DisputeReason { get; set; }
    [JsonPropertyName("reversedAt")] public DateTime? ReversedAt { get; set; }
    [JsonPropertyName("voidedAt")] public DateTime? VoidedAt { get; set; }
    [JsonPropertyName("voidReason")] public string? VoidReason { get; set; }
}


/// <summary>Filters for the commission ledger history.</summary>
public class CommissionEntriesQueryRequest
{
    [JsonPropertyName("staffUserId")] public Guid? StaffUserId { get; set; }
    [JsonPropertyName("serviceId")] public Guid? ServiceId { get; set; }
    [JsonPropertyName("appointmentId")] public Guid? AppointmentId { get; set; }
    [JsonPropertyName("customerId")] public Guid? CustomerId { get; set; }
    /// <summary>Comma-separated statuses, e.g. <c>pending,approved</c>.</summary>
    [JsonPropertyName("status")] public string? Status { get; set; }
    /// <summary><c>earning</c> | <c>adjustment</c> | <c>reversal</c> (comma-separated).</summary>
    [JsonPropertyName("entryType")] public string? EntryType { get; set; }
    [JsonPropertyName("from")] public DateTime? From { get; set; }
    [JsonPropertyName("to")] public DateTime? To { get; set; }
    [JsonPropertyName("sortBy")] public string? SortBy { get; set; }
    [JsonPropertyName("sortDirection")] public string? SortDirection { get; set; }
    [JsonPropertyName("page")] public int Page { get; set; } = 1;
    [JsonPropertyName("pageSize")] public int PageSize { get; set; } = 25;
}

/// <summary>Amounts owed to (or held from) one staff member.</summary>
public class CommissionStaffBalanceResponse
{
    [JsonPropertyName("staffUserId")] public Guid StaffUserId { get; set; }
    [JsonPropertyName("staffName")] public string? StaffName { get; set; }
    [JsonPropertyName("currency")] public string Currency { get; set; } = "KES";
    [JsonPropertyName("pendingAmount")] public decimal PendingAmount { get; set; }
    [JsonPropertyName("approvedAmount")] public decimal ApprovedAmount { get; set; }
    [JsonPropertyName("payableAmount")] public decimal PayableAmount { get; set; }
    [JsonPropertyName("paidAmount")] public decimal PaidAmount { get; set; }
    [JsonPropertyName("disputedAmount")] public decimal DisputedAmount { get; set; }
    [JsonPropertyName("reversedAmount")] public decimal ReversedAmount { get; set; }
    /// <summary>Payable + approved: what the business can settle right now.</summary>
    [JsonPropertyName("outstandingAmount")] public decimal OutstandingAmount { get; set; }
    [JsonPropertyName("lifetimeEarnedAmount")] public decimal LifetimeEarnedAmount { get; set; }
}

/// <summary>Business-level commission report over a period.</summary>
public class CommissionReportResponse
{
    [JsonPropertyName("businessId")] public Guid BusinessId { get; set; }
    [JsonPropertyName("from")] public DateTime? From { get; set; }
    [JsonPropertyName("to")] public DateTime? To { get; set; }
    [JsonPropertyName("currency")] public string Currency { get; set; } = "KES";
    [JsonPropertyName("totalAccruedAmount")] public decimal TotalAccruedAmount { get; set; }
    [JsonPropertyName("totalApprovedAmount")] public decimal TotalApprovedAmount { get; set; }
    [JsonPropertyName("totalPayableAmount")] public decimal TotalPayableAmount { get; set; }
    [JsonPropertyName("totalPaidAmount")] public decimal TotalPaidAmount { get; set; }
    [JsonPropertyName("totalReversedAmount")] public decimal TotalReversedAmount { get; set; }
    [JsonPropertyName("totalVoidedAmount")] public decimal TotalVoidedAmount { get; set; }
    [JsonPropertyName("disputedAmount")] public decimal DisputedAmount { get; set; }
    [JsonPropertyName("entryCount")] public int EntryCount { get; set; }
    [JsonPropertyName("byStaff")] public List<CommissionStaffBalanceResponse> ByStaff { get; set; } = new();
}


// ═══════════════════════════════════════════════════════════════
//  COMMISSIONS — LIFECYCLE ACTIONS
// ═══════════════════════════════════════════════════════════════

/// <summary>Releases pending entries (batch approval).</summary>
public class ApproveCommissionEntriesRequest
{
    /// <summary>Explicit entry ids. Omit to approve every eligible entry matching the filters.</summary>
    [JsonPropertyName("entryIds")] public Guid[]? EntryIds { get; set; }
    [JsonPropertyName("staffUserId")] public Guid? StaffUserId { get; set; }
    [JsonPropertyName("to")] public DateTime? To { get; set; }
    [JsonPropertyName("note")] public string? Note { get; set; }
}

/// <summary>Records a payout against a staff member's payable entries.</summary>
public class RecordCommissionPayoutRequest
{
    [JsonPropertyName("staffUserId")] public Guid StaffUserId { get; set; }

    /// <summary>External payment reference (M-PESA code, bank ref, "CASH"). Required and unique per business.</summary>
    [JsonPropertyName("reference")] public string Reference { get; set; } = string.Empty;

    /// <summary>Explicit entries to settle. Omit to settle every payable entry.</summary>
    [JsonPropertyName("entryIds")] public Guid[]? EntryIds { get; set; }
    [JsonPropertyName("periodStart")] public DateTime? PeriodStart { get; set; }
    [JsonPropertyName("periodEnd")] public DateTime? PeriodEnd { get; set; }
    [JsonPropertyName("notes")] public string? Notes { get; set; }
}

/// <summary>A recorded payout batch.</summary>
public class CommissionPayoutResponse
{
    [JsonPropertyName("id")] public Guid Id { get; set; }
    [JsonPropertyName("businessId")] public Guid BusinessId { get; set; }
    [JsonPropertyName("staffUserId")] public Guid StaffUserId { get; set; }
    [JsonPropertyName("staffName")] public string? StaffName { get; set; }
    [JsonPropertyName("reference")] public string Reference { get; set; } = string.Empty;
    [JsonPropertyName("notes")] public string? Notes { get; set; }
    [JsonPropertyName("totalAmount")] public decimal TotalAmount { get; set; }
    [JsonPropertyName("currency")] public string Currency { get; set; } = "KES";
    [JsonPropertyName("entryCount")] public int EntryCount { get; set; }
    [JsonPropertyName("periodStart")] public DateTime? PeriodStart { get; set; }
    [JsonPropertyName("periodEnd")] public DateTime? PeriodEnd { get; set; }
    [JsonPropertyName("paidAt")] public DateTime PaidAt { get; set; }
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; }
    [JsonPropertyName("entryIds")] public List<Guid> EntryIds { get; set; } = new();
}

/// <summary>Adjusts a staff member's commission. A reason is mandatory.</summary>
public class CommissionAdjustmentRequest
{
    [JsonPropertyName("staffUserId")] public Guid StaffUserId { get; set; }
    /// <summary>Signed amount: positive adds commission, negative withholds it.</summary>
    [JsonPropertyName("amount")] public decimal Amount { get; set; }
    [JsonPropertyName("reason")] public string Reason { get; set; } = string.Empty;
    /// <summary>Optional earning this adjustment corrects.</summary>
    [JsonPropertyName("adjustmentOfEntryId")] public Guid? AdjustmentOfEntryId { get; set; }
    [JsonPropertyName("appointmentId")] public Guid? AppointmentId { get; set; }
    [JsonPropertyName("serviceId")] public Guid? ServiceId { get; set; }
    [JsonPropertyName("releaseImmediately")] public bool ReleaseImmediately { get; set; }
}

/// <summary>Reverses a single entry (refund, correction, dispute resolution).</summary>
public class CommissionReversalRequest
{
    [JsonPropertyName("reason")] public string Reason { get; set; } = string.Empty;
}

/// <summary>Raises a dispute on an entry.</summary>
public class CommissionDisputeRequest
{
    [JsonPropertyName("reason")] public string Reason { get; set; } = string.Empty;
}

/// <summary>Resolves a dispute: release the commission or void it.</summary>
public class ResolveCommissionDisputeRequest
{
    /// <summary>True releases the entry as payable; false voids it.</summary>
    [JsonPropertyName("approve")] public bool Approve { get; set; }
    [JsonPropertyName("note")] public string? Note { get; set; }
}
