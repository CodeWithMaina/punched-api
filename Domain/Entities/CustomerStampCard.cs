using System.ComponentModel.DataAnnotations;

namespace PunchedApi.Domain.Entities;

/// <summary>
/// Explicit persisted ownership of a stamp card template by a customer.
/// Business is derived through StampCard.BusinessId (never duplicated here).
/// Requires an Active CustomerBusinessEnrollment for the card's business.
/// </summary>
public class CustomerStampCard : BaseEntity
{
    [Required]
    public Guid CustomerId { get; set; }

    [Required]
    public Guid StampCardId { get; set; }

    [Required]
    public CustomerStampCardStatus Status { get; set; } = CustomerStampCardStatus.Active;

    [Required]
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LeftAt { get; set; }

    [Required]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public virtual User Customer { get; set; } = null!;
    public virtual StampCard StampCard { get; set; } = null!;
}
