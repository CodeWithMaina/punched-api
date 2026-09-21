using System.ComponentModel.DataAnnotations;

namespace PunchedApi.Domain.Entities;

/// <summary>
/// Explicit persisted membership of a customer in a business.
/// This is the authoritative Customer ↔ Business relationship: bookings gate on it,
/// loyalty enrollment implies it, and leaving preserves all history rows.
/// One row per (Customer, Business); re-enroll reactivates the same row.
/// </summary>
public class CustomerBusinessEnrollment : BaseEntity
{
    [Required]
    public Guid CustomerId { get; set; }

    [Required]
    public Guid BusinessId { get; set; }

    [Required]
    public CustomerBusinessEnrollmentStatus Status { get; set; } = CustomerBusinessEnrollmentStatus.Active;

    /// <summary>How the enrollment was created: qr|discovery|business|booking|referral|loyalty.</summary>
    [Required]
    [MaxLength(20)]
    public string Source { get; set; } = "discovery";

    [Required]
    public DateTime EnrolledAt { get; set; } = DateTime.UtcNow;

    public DateTime? LeftAt { get; set; }

    [Required]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public virtual User Customer { get; set; } = null!;
    public virtual Business Business { get; set; } = null!;
}
