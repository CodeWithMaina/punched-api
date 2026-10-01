using System.ComponentModel.DataAnnotations;

namespace PunchedApi.Domain.Entities;

public class PushDevice : BaseEntity
{
    [Required]
    public Guid UserId { get; set; }

    [Required]
    [MaxLength(2048)]
    public string Endpoint { get; set; } = string.Empty;

    [Required]
    [MaxLength(255)]
    public string P256dh { get; set; } = string.Empty;

    [Required]
    [MaxLength(255)]
    public string Auth { get; set; } = string.Empty;

    [MaxLength(512)]
    public string? UserAgent { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
}
