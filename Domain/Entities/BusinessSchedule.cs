using System.ComponentModel.DataAnnotations;

namespace PunchedApi.Domain.Entities;

/// <summary>
/// One weekday in the business's recurring operating schedule.
///
/// <para>This is the BUSINESS-level pattern (as distinct from a staff member's
/// <see cref="StaffAvailability"/>). It constrains every staff window: a slot is
/// bookable only where the business is open AND the staff member is working.
/// A day with no row is treated as "not configured", which preserves the
/// pre-existing BookingOpenHour/CloseHour behaviour.</para>
/// </summary>
public class BusinessOperatingHour : BaseEntity
{
    [Required]
    public Guid BusinessId { get; set; }

    /// <summary>0 = Sunday ... 6 = Saturday (mirrors <see cref="System.DayOfWeek"/>).</summary>
    [Range(0, 6)]
    public int DayOfWeek { get; set; }

    /// <summary>True marks the whole day closed; the periods are then ignored.</summary>
    public bool IsClosed { get; set; }

    public virtual ICollection<BusinessOperatingPeriod> Periods { get; set; } = new List<BusinessOperatingPeriod>();
}

/// <summary>A working period inside one weekday (minutes from business-local midnight).</summary>
public class BusinessOperatingPeriod : BaseEntity
{
    [Required]
    public Guid BusinessOperatingHourId { get; set; }

    [Range(0, 1439)]
    public int StartMinute { get; set; }

    [Range(1, 1440)]
    public int EndMinute { get; set; }

    public virtual BusinessOperatingHour OperatingHour { get; set; } = null!;
}

/// <summary>
/// A dated override of the weekly schedule: a public holiday, a temporary
/// closure, or special opening hours. A row for a date wins over the weekday.
/// </summary>
public class BusinessScheduleException : BaseEntity
{
    [Required]
    public Guid BusinessId { get; set; }

    public DateOnly Date { get; set; }

    /// <summary>True closes the whole date; the periods are then ignored.</summary>
    public bool IsClosed { get; set; }

    [MaxLength(200)]
    public string? Reason { get; set; }

    public virtual ICollection<BusinessScheduleExceptionPeriod> Periods { get; set; } =
        new List<BusinessScheduleExceptionPeriod>();
}

/// <summary>A working period for a dated exception (minutes from local midnight).</summary>
public class BusinessScheduleExceptionPeriod : BaseEntity
{
    [Required]
    public Guid BusinessScheduleExceptionId { get; set; }

    [Range(0, 1439)]
    public int StartMinute { get; set; }

    [Range(1, 1440)]
    public int EndMinute { get; set; }

    public virtual BusinessScheduleException Exception { get; set; } = null!;
}
