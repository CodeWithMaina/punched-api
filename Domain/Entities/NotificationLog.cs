using System.ComponentModel.DataAnnotations;

namespace PunchedApi.Domain.Entities;

public class NotificationLog : BaseEntity
{
    [Required]
    public Guid UserId { get; set; }

    public Guid? BusinessId { get; set; }

    [Required]
    [MaxLength(20)]
    public string Channel { get; set; } = "email";

    [Required]
    [MaxLength(100)]
    public string TemplateType { get; set; } = string.Empty;

    [Required]
    [MaxLength(20)]
    public string Status { get; set; } = "sent";

    public DateTime SentAt { get; set; } = DateTime.UtcNow;
    public DateTime? DeliveredAt { get; set; }
    public DateTime? OpenedAt { get; set; }

    /// <summary>JSON payload handed to an external channel by the delivery worker.</summary>
    public string PayloadJson { get; set; } = "{}";

    /// <summary>Number of failed delivery attempts made so far.</summary>
    public int Attempts { get; set; }

    /// <summary>Earliest UTC time at which a pending row may be claimed.</summary>
    public DateTime NextAttemptAt { get; set; } = DateTime.UtcNow;

    /// <summary>Caller key or deterministic natural key used to collapse duplicate sends.</summary>
    [MaxLength(200)]
    public string? IdempotencyKey { get; set; }

    /// <summary>Last ledger state transition timestamp.</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(500)]
    public string? Error { get; set; }
}
