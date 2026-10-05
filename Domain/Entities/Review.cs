using System.ComponentModel.DataAnnotations;

namespace PunchedApi.Domain.Entities;

public static class ReviewStatuses
{
    public const string Published = "published";
    public const string Hidden = "hidden";
    public const string Removed = "removed";
}

public class Review : BaseEntity
{
    [Required]
    public Guid AppointmentId { get; set; }

    [Required]
    public Guid BusinessId { get; set; }

    [Required]
    public Guid CustomerId { get; set; }

    public Guid? StaffUserId { get; set; }

    [Range(1, 5)]
    public int Rating { get; set; }

    [MaxLength(500)]
    public string? Comment { get; set; }

    [Required]
    [MaxLength(20)]
    public string Status { get; set; } = ReviewStatuses.Published;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
