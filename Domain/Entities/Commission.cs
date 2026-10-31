using System.ComponentModel.DataAnnotations;

namespace PunchedApi.Domain.Entities;

/// <summary>How a commission amount is derived from its calculation base.</summary>
public enum CommissionCalculationType
{
    /// <summary>A percentage of the qualifying base (e.g. 12.5%).</summary>
    Percentage = 0,

    /// <summary>A flat amount per qualifying service line, independent of price.</summary>
    Fixed = 1
}

/// <summary>
/// Specificity of a commission arrangement. Precedence is resolved by
/// specificity (see <c>CommissionResolver</c>):
/// <c>StaffService &gt; Service &gt; Staff &gt; Business</c>.
/// </summary>
public enum CommissionScope
{
    /// <summary>The business-wide default arrangement.</summary>
    Business = 0,

    /// <summary>Overrides the business default for one staff member (all services).</summary>
    Staff = 1,

    /// <summary>Overrides the business default for one service (all staff).</summary>
    Service = 2,

    /// <summary>Most specific: one staff member on one service.</summary>
    StaffService = 3
}

/// <summary>
/// Lifecycle of a single commission ledger entry.
///
/// <para><b>Pending → Approved → Payable → Paid</b> is the happy path. A
/// <see cref="Pending"/> entry has been accrued (the qualifying appointment
/// completed) but is not yet a settled earning. <see cref="Disputed"/> is a
/// reversible hold raised by the business; <see cref="Reversed"/> and
/// <see cref="Void"/> are terminal.</para>
///
/// <para>Entries are append-only in spirit: an already-settled entry is never
/// rewritten to a different amount. Corrections happen by appending an
/// <see cref="CommissionEntryType.Adjustment"/> or
/// <see cref="CommissionEntryType.Reversal"/> entry that references the
/// original, so the audit trail always explains both the original calculation
/// and the correction.</para>
/// </summary>
public enum CommissionEntryStatus
{
    /// <summary>Accrued from a qualifying event; not yet a settled earning.</summary>
    Pending = 0,

    /// <summary>Qualifies and has been approved (automatically or manually).</summary>
    Approved = 1,

    /// <summary>Ready to be included in a staff payout.</summary>
    Payable = 2,

    /// <summary>Included in a recorded payout.</summary>
    Paid = 3,

    /// <summary>Under dispute; excluded from balances until resolved.</summary>
    Disputed = 4,

    /// <summary>Terminal: reversed (refund, payment reversal, appointment change).</summary>
    Reversed = 5,

    /// <summary>Terminal: never qualified (cancelled appointment, ineligible service, no-show policy).</summary>
    Void = 6
}

/// <summary>What produced a ledger entry.</summary>
public enum CommissionEntryType
{
    /// <summary>An earning accrued from a qualifying appointment/service.</summary>
    Earning = 0,

    /// <summary>A manual correction (may be positive or negative) with a mandatory reason.</summary>
    Adjustment = 1,

    /// <summary>A reversal of a previously accrued earning (negative amount).</summary>
    Reversal = 2
}

/// <summary>The domain record a ledger entry is derived from.</summary>
public enum CommissionSourceType
{
    /// <summary>Derived from an appointment's service lines (automatic).</summary>
    Appointment = 0,

    /// <summary>Entered by a business user (manual adjustment).</summary>
    Manual = 1
}

/// <summary>
/// Per-business commission configuration. Exactly one row per business
/// (enforced by a unique index). Absence of a row means "commissions are not
/// configured", which the service treats as <see cref="IsEnabled"/> = false —
/// never as an implicit percentage.
/// </summary>
public class CommissionConfig : BaseEntity
{
    [Required]
    public Guid BusinessId { get; set; }

    /// <summary>Master switch. When false no entries are ever accrued.</summary>
    public bool IsEnabled { get; set; }

    /// <summary>Default arrangement used when no staff/service override applies.</summary>
    [Required]
    public CommissionCalculationType CalculationType { get; set; } = CommissionCalculationType.Percentage;

    /// <summary>Default percentage (e.g. 12.5000 = 12.5%). Required when the type is Percentage.</summary>
    public decimal? DefaultPercentage { get; set; }

    /// <summary>Default flat amount per qualifying service line. Required when the type is Fixed.</summary>
    public decimal? DefaultFixedAmount { get; set; }

    /// <summary>ISO-4217 currency of every entry accrued under this configuration.</summary>
    [Required]
    [MaxLength(3)]
    public string Currency { get; set; } = "KES";

    /// <summary>
    /// When true (default) an accrued earning stays <see cref="CommissionEntryStatus.Pending"/>
    /// until the appointment's payments settle it. When false the earning is
    /// released as soon as the qualifying appointment completes.
    /// </summary>
    public bool RequireSettlement { get; set; } = true;

    /// <summary>When true an earning must be released by an authorised user before it becomes payable.</summary>
    public bool RequireApproval { get; set; }

    /// <summary>Whether an appointment marked no-show still earns commission.</summary>
    public bool EarnOnNoShow { get; set; }

    /// <summary>Whether a cancelled appointment earns commission. Defaults to false.</summary>
    public bool EarnOnCancelled { get; set; }

    /// <summary>
    /// Whether a partially settled appointment may release commission. When
    /// false, commission is only released once the settlement ratio reaches
    /// <see cref="MinimumSettlementRatio"/>.
    /// </summary>
    public bool AllowPartialSettlement { get; set; } = true;

    /// <summary>
    /// Fraction of the appointment's value that must be settled before
    /// commission is released (0 = any successful payment, 1 = fully paid).
    /// </summary>
    public decimal MinimumSettlementRatio { get; set; }

    /// <summary>
    /// When true (default) commission is only earned for a service the assigned
    /// staff member is eligible to perform (an active
    /// <see cref="StaffServiceAssignment"/> exists). This keeps commission
    /// aligned with the same eligibility rule the booking engine enforces.
    /// </summary>
    public bool RequireStaffServiceEligibility { get; set; } = true;

    /// <summary>Rounding precision applied to calculated amounts (0–4).</summary>
    public int RoundingDecimals { get; set; } = 2;

    [MaxLength(300)]
    public string? Notes { get; set; }

    [Required]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public virtual Business Business { get; set; } = null!;
}

/// <summary>
/// A scoped override of the business default commission arrangement.
///
/// <para>Rows are made unambiguous by <see cref="ScopeKey"/>, a deterministic
/// string derived from the scope plus its identifiers, which carries a unique
/// index together with <see cref="BusinessId"/>. A second, conflicting
/// arrangement for the same scope is therefore rejected by the database rather
/// than silently winning.</para>
/// </summary>
public class CommissionRate : BaseEntity
{
    [Required]
    public Guid BusinessId { get; set; }

    [Required]
    public CommissionScope Scope { get; set; }

    /// <summary>Set for <see cref="CommissionScope.Staff"/> and <see cref="CommissionScope.StaffService"/>.</summary>
    public Guid? StaffUserId { get; set; }

    /// <summary>Set for <see cref="CommissionScope.Service"/> and <see cref="CommissionScope.StaffService"/>.</summary>
    public Guid? ServiceCatalogItemId { get; set; }

    /// <summary>Deterministic uniqueness key: <c>business</c> | <c>staff:{id}</c> | <c>service:{id}</c> | <c>staff:{id}:service:{id}</c>.</summary>
    [Required]
    [MaxLength(120)]
    public string ScopeKey { get; set; } = string.Empty;

    [Required]
    public CommissionCalculationType CalculationType { get; set; } = CommissionCalculationType.Percentage;

    public decimal? Percentage { get; set; }

    public decimal? FixedAmount { get; set; }

    public bool IsActive { get; set; } = true;

    [Required]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public virtual Business Business { get; set; } = null!;

    /// <summary>
    /// Builds the deterministic uniqueness key for a scope. Kept on the entity
    /// so every writer produces exactly the same key shape.
    /// </summary>
    public static string BuildScopeKey(CommissionScope scope, Guid? staffUserId, Guid? serviceCatalogItemId) => scope switch
    {
        CommissionScope.Staff => $"staff:{staffUserId}",
        CommissionScope.Service => $"service:{serviceCatalogItemId}",
        CommissionScope.StaffService => $"staff:{staffUserId}:service:{serviceCatalogItemId}",
        _ => "business"
    };
}

/// <summary>
/// <b>The immutable commission ledger.</b>
///
/// <para>Every entry stores the complete calculation context — the
/// configuration that was applied (<see cref="ConfigSnapshotJson"/>), the rate
/// or fixed amount actually used, the base it was applied to, the resulting
/// amount and the source record. Changing a commission rate later therefore
/// never rewrites history: a new rate only affects entries created after the
/// change.</para>
///
/// <para><see cref="DedupeKey"/> carries a unique index so a retried payment
/// callback, a repeated completion call or a re-run background job can never
/// produce a second entry for the same economic event.</para>
/// </summary>
public class CommissionEntry : BaseEntity
{
    [Required]
    public Guid BusinessId { get; set; }

    /// <summary>The staff member who earns (or loses) the commission.</summary>
    [Required]
    public Guid StaffUserId { get; set; }

    public Guid? CustomerId { get; set; }

    public Guid? AppointmentId { get; set; }

    /// <summary>The specific appointment service line this entry was accrued from.</summary>
    public Guid? AppointmentResourceId { get; set; }

    public Guid? ServiceCatalogItemId { get; set; }

    /// <summary>The settling payment (null while unsettled).</summary>
    public Guid? PaymentId { get; set; }

    /// <summary>The payout batch that included this entry (null until paid).</summary>
    public Guid? PayoutId { get; set; }

    [Required]
    public CommissionEntryType EntryType { get; set; } = CommissionEntryType.Earning;

    [Required]
    public CommissionSourceType SourceType { get; set; } = CommissionSourceType.Appointment;

    [Required]
    public CommissionEntryStatus Status { get; set; } = CommissionEntryStatus.Pending;

    /// <summary>The specificity of the configuration that produced this entry.</summary>
    [Required]
    public CommissionScope ConfigScope { get; set; } = CommissionScope.Business;

    /// <summary>Id of the <see cref="CommissionConfig"/> or <see cref="CommissionRate"/> applied.</summary>
    public Guid? ConfigId { get; set; }

    /// <summary>Immutable JSON snapshot of the applied configuration (rate, scope, flags).</summary>
    [Required]
    public string ConfigSnapshotJson { get; set; } = "{}";

    [Required]
    public CommissionCalculationType CalculationType { get; set; } = CommissionCalculationType.Percentage;

    /// <summary>Percentage applied (e.g. 12.5000), or null for a fixed arrangement.</summary>
    public decimal? RatePercent { get; set; }

    /// <summary>Fixed amount applied, or null for a percentage arrangement.</summary>
    public decimal? FixedAmount { get; set; }

    /// <summary>The qualifying base the rate/amount was applied to.</summary>
    [Required]
    public decimal BaseAmount { get; set; }

    /// <summary>The signed commission amount. Negative for reversals and downward adjustments.</summary>
    [Required]
    public decimal Amount { get; set; }

    [Required]
    [MaxLength(3)]
    public string Currency { get; set; } = "KES";

    /// <summary>Mandatory explanation for adjustments and reversals.</summary>
    [MaxLength(300)]
    public string? Reason { get; set; }

    /// <summary>For adjustments: the earning being corrected.</summary>
    public Guid? AdjustmentOfEntryId { get; set; }

    /// <summary>For reversals: the earning being reversed.</summary>
    public Guid? ReversesEntryId { get; set; }

    /// <summary>On the original earning: the entry that reversed it (set once).</summary>
    public Guid? ReversedByEntryId { get; set; }

    /// <summary>Unique idempotency key for the economic event that produced this entry.</summary>
    [Required]
    [MaxLength(200)]
    public string DedupeKey { get; set; } = string.Empty;

    /// <summary>Who created a manual entry (null for automatic accruals).</summary>
    public Guid? CreatedByUserId { get; set; }

    public DateTime? ApprovedAt { get; set; }
    public Guid? ApprovedByUserId { get; set; }
    public DateTime? PayableAt { get; set; }
    public DateTime? PaidAt { get; set; }
    public DateTime? DisputedAt { get; set; }
    public Guid? DisputedByUserId { get; set; }

    [MaxLength(300)]
    public string? DisputeReason { get; set; }

    public DateTime? ReversedAt { get; set; }
    public DateTime? VoidedAt { get; set; }

    [MaxLength(300)]
    public string? VoidReason { get; set; }

    [Required]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Optimistic-concurrency token. Every lifecycle write increments it, so two
    /// concurrent approvals/payouts for the same entry cannot both succeed.
    /// </summary>
    public int Version { get; set; }

    public virtual Business Business { get; set; } = null!;
}

/// <summary>
/// A recorded payout batch: the business telling the platform "these payable
/// commission entries were paid to this staff member, under this reference".
///
/// <para><b>This is not a money transfer.</b> The platform has no staff payout
/// provider; recording a payout attributes already-earned commission to a real
/// payment the business made (M-PESA, bank, cash). Only integrate execution
/// here if a payout provider is introduced.</para>
/// </summary>
public class CommissionPayout : BaseEntity
{
    [Required]
    public Guid BusinessId { get; set; }

    [Required]
    public Guid StaffUserId { get; set; }

    /// <summary>External payment reference (M-PESA code, bank ref, "CASH"). Unique per business.</summary>
    [Required]
    [MaxLength(100)]
    public string Reference { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Notes { get; set; }

    /// <summary>Sum of the entry amounts settled by this payout.</summary>
    [Required]
    public decimal TotalAmount { get; set; }

    [Required]
    [MaxLength(3)]
    public string Currency { get; set; } = "KES";

    /// <summary>Number of ledger entries settled by this payout.</summary>
    public int EntryCount { get; set; }

    public DateTime? PeriodStartUtc { get; set; }
    public DateTime? PeriodEndUtc { get; set; }

    [Required]
    public DateTime PaidAtUtc { get; set; } = DateTime.UtcNow;

    public Guid? CreatedByUserId { get; set; }

    public virtual Business Business { get; set; } = null!;
}

