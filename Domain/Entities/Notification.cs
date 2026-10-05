using System.ComponentModel.DataAnnotations;

namespace PunchedApi.Domain.Entities;

/// <summary>
/// In-app notification for a staff user (e.g. daily goal reached, reward ready).
/// </summary>
public class Notification : BaseEntity
{
    /// <summary>The staff user who receives this notification.</summary>
    [Required]
    public Guid UserId { get; set; }

    /// <summary>The business context (nullable for platform-wide notifications).</summary>
    public Guid? BusinessId { get; set; }

    /// <summary>Notification type: "GoalReached" or "RewardReady".</summary>
    [Required]
    [MaxLength(50)]
    public string Type { get; set; } = string.Empty;

    /// <summary>Stamp count associated with the notification (e.g. stamps to reach goal).</summary>
    public int StampsCount { get; set; }

    /// <summary>
    /// Optional appointment context. Set for appointment lifecycle notifications
    /// (reschedule requested/approved/rejected) so clients can deep-link.
    /// </summary>
    public Guid? AppointmentId { get; set; }

    /// <summary>Whether the user has dismissed/read this notification.</summary>
    public bool IsRead { get; set; } = false;

    /// <summary>
    /// Display payload (<c>jsonb</c>, defaults to <c>{}</c>) so the row renders
    /// without a join — e.g. <c>{businessName, appointmentId, stamps}</c>.
    /// Populated by <c>INotificationService.SendAsync</c> from the request data.
    /// </summary>
    public string PayloadJson { get; set; } = "{}";

    /// <summary>
    /// Set when the user archives the row. Archived rows leave the default
    /// inbox list without a status enum.
    /// </summary>
    public DateTime? ArchivedAt { get; set; }
}