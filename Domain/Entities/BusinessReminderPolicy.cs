using System.ComponentModel.DataAnnotations;

namespace PunchedApi.Domain.Entities;

/// <summary>
/// Business-level reminder policy (Phase 2): which offsets before the appointment
/// the reminder ladder fires, on which channels, and whether reminders are enabled
/// at all. Absence of a row means platform defaults (T-24h and T-1h, in-app +
/// email + push). Interim home until Phase 4's business messaging profile takes
/// ownership of messaging policy (documented in the implementation plan).
/// </summary>
public class BusinessReminderPolicy
{
    /// <summary>Policy owner (PK + FK to businesses).</summary>
    public Guid BusinessId { get; set; }

    /// <summary>False disables the reminder ladder for the business.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>JSON int array of offsets in minutes before ScheduledAt — default <c>[1440,60]</c>.</summary>
    [Required]
    public string OffsetMinutesJson { get; set; } = "[1440,60]";

    /// <summary>JSON channel allow-list — default <c>["in_app","email","push"]</c>.</summary>
    [Required]
    public string ChannelsJson { get; set; } = "[\"in_app\",\"email\",\"push\"]";

    /// <summary>Last write timestamp.</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}