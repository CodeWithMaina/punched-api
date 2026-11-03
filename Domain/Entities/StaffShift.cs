using System.ComponentModel.DataAnnotations;

namespace PunchedApi.Domain.Entities;

/// <summary>
/// One rostered day for a staff member.
///
/// <para>Two shapes share this table, deliberately:</para>
/// <list type="bullet">
/// <item>A <b>pattern</b> row (see <see cref="StaffAvailability"/>) describes the
/// recurring week — one row per weekday.</item>
/// <item>A <b>dated</b> row describes what actually happened on one calendar
/// date: a one-off override, or a day off (time off) when
/// <see cref="IsWorking"/> is false.</item>
/// </list>
///
/// <para><b>Resolution order</b> (see <c>AppointmentAvailabilityService</c>): a
/// dated row for the date wins; otherwise the weekday pattern row; otherwise the
/// business's own booking window. That order is what lets a single day off
/// override a weekly pattern without deleting it.</para>
///
/// <para><see cref="StartHour"/>/<see cref="EndHour"/> are kept as the coarse,
/// always-present representation so every pre-existing row (and every reader that
/// has not been taught about minutes) keeps working; the optional minute columns
/// refine the same window when present.</para>
/// </summary>
public class StaffShift : BaseEntity
{
    [Required]
    public Guid StaffUserId { get; set; }

    [Required]
    public Guid BusinessId { get; set; }

    [Required]
    public DateOnly Date { get; set; }

    [Range(0, 23)]
    public int StartHour { get; set; }

    [Range(0, 23)]
    public int EndHour { get; set; }

    /// <summary>Minute-precise shift start (0–1439). Falls back to <see cref="StartHour"/> * 60.</summary>
    [Range(0, 1439)]
    public int? StartMinute { get; set; }

    /// <summary>Minute-precise shift end (0–1439). Falls back to <see cref="EndHour"/> * 60.</summary>
    [Range(0, 1439)]
    public int? EndMinute { get; set; }

    /// <summary>Optional unpaid break start; must fall inside the shift.</summary>
    [Range(0, 1439)]
    public int? BreakStartMinute { get; set; }

    /// <summary>Optional unpaid break end; must fall inside the shift.</summary>
    [Range(0, 1439)]
    public int? BreakEndMinute { get; set; }

    /// <summary>Why this date differs from the pattern, e.g. "Annual leave".</summary>
    [MaxLength(200)]
    public string? Note { get; set; }

    public bool IsWorking { get; set; } = true;
}

