using System.ComponentModel.DataAnnotations;

namespace PunchedApi.Domain.Entities;

/// <summary>
/// A staff member's recurring working week: exactly one row per weekday
/// (<see cref="DayOfWeek"/>, 0 = Sunday … 6 = Saturday, matching
/// <see cref="System.DayOfWeek"/>).
///
/// <para>This is the <b>pattern</b>. A dated <see cref="StaffShift"/> row for a
/// specific calendar date is an <b>override</b> — a one-off change or a day off —
/// and always wins over the pattern for that date. With neither present, the
/// business's own booking window applies, which is exactly how availability
/// behaved before this table existed.</para>
///
/// <para>Times are minutes from local midnight in the business's time zone, so a
/// shift may start at 09:15 without inventing a second time representation.</para>
/// </summary>
public class StaffAvailability : BaseEntity
{
    [Required]
    public Guid BusinessId { get; set; }

    [Required]
    public Guid StaffUserId { get; set; }

    /// <summary>0 = Sunday … 6 = Saturday (mirrors <see cref="System.DayOfWeek"/>).</summary>
    [Range(0, 6)]
    public int DayOfWeek { get; set; }

    /// <summary>False marks a recurring day off; the times below are then ignored.</summary>
    public bool IsWorking { get; set; } = true;

    /// <summary>Start of the working window, in minutes from local midnight (0–1439).</summary>
    [Range(0, 1439)]
    public int StartMinute { get; set; }

    /// <summary>End of the working window, in minutes from local midnight (1–1440).</summary>
    [Range(1, 1440)]
    public int EndMinute { get; set; }

    /// <summary>Optional unpaid break start, inside the working window.</summary>
    [Range(0, 1439)]
    public int? BreakStartMinute { get; set; }

    /// <summary>Optional unpaid break end, inside the working window.</summary>
    [Range(0, 1439)]
    public int? BreakEndMinute { get; set; }

    [Required]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
