using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PunchedApi.Domain.Entities;

/// <summary>
/// Per-business automation rule (Phase 3). Rules are declarative: trigger ->
/// conditions -> actions -> recipients. Stored as validated JSON; the evaluator
/// interprets them at runtime. No arbitrary code, SQL, or URLs are ever accepted.
/// </summary>
[Table("automation_rules")]
public class AutomationRule : BaseEntity
{
    /// <summary>Tenant scope — every rule belongs to exactly one business.</summary>
    [Required]
    public Guid BusinessId { get; set; }

    /// <summary>Human-readable rule name (unique per business).</summary>
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional description for the business owner.</summary>
    [MaxLength(1000)]
    public string? Description { get; set; }

    /// <summary>
    /// Dotted trigger name, e.g. <c>appointment.created</c>,
    /// <c>appointment.rescheduled</c>, <c>payment.failed</c>, <c>time.before_appointment</c>.
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string Trigger { get; set; } = string.Empty;

    /// <summary>
    /// Serialized condition predicates (ANDed). Validated against the condition
    /// schema at save time; unknown fields cause rejection.
    /// </summary>
    [Required]
    public string ConditionsJson { get; set; } = "{}";

    /// <summary>
    /// Serialized allowlisted action list. Actions are executed in order.
    /// </summary>
    [Required]
    public string ActionsJson { get; set; } = "[]";

    /// <summary>
    /// Serialized recipient categories (customer, staff.assigned, business.owner,
    /// business.all_staff, segment:{id}). Resolved at execution time.
    /// </summary>
    [Required]
    public string RecipientsJson { get; set; } = "[]";

    /// <summary>Whether the rule is currently enabled.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Rule version — incremented on each material change. Snapshotted per run.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Per-business daily action cap (null = no cap).</summary>
    public int? DailyActionCap { get; set; }

    /// <summary>Per-customer frequency cap in minutes (null = no cap).</summary>
    public int? FrequencyCapMinutes { get; set; }

    /// <summary>Quiet hours start (business timezone, null = none).</summary>
    public TimeSpan? QuietHoursStart { get; set; }

    /// <summary>Quiet hours end (business timezone, null = none).</summary>
    public TimeSpan? QuietHoursEnd { get; set; }

    /// <summary>Last write timestamp.</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Foreign key to businesses.</summary>
    [ForeignKey(nameof(BusinessId))]
    public virtual Business? Business { get; set; }
}