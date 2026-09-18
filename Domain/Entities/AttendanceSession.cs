namespace PunchedApi.Domain.Entities;

/// <summary>
/// Persisted attendance session (a first-class row rather than a query over
/// events): answers "is this staff member clocked in right now?" with an
/// indexable single row, makes duplicate clock-in impossible at the DB
/// level via a partial unique index, and gives breaks/overtime/analytics a
/// clean unit to attach to.
/// </summary>
public class AttendanceSession : BaseEntity
{
    /// <summary>
    /// FK to the business this session belongs to.
    /// </summary>
    public Guid BusinessId { get; set; }

    /// <summary>
    /// FK to the staff user. Always server-derived — never client-supplied.
    /// </summary>
    public Guid StaffUserId { get; set; }

    /// <summary>
    /// FK to the opening CLOCK_IN event.
    /// </summary>
    public Guid OpeningEventId { get; set; }

    /// <summary>
    /// FK to the closing CLOCK_OUT event. Null while open.
    /// </summary>
    public Guid? ClosingEventId { get; set; }

    /// <summary>
    /// UTC timestamp the session opened.
    /// </summary>
    public DateTime OpenedAt { get; set; }

    /// <summary>
    /// UTC timestamp the session closed. Null = open / incomplete.
    /// </summary>
    public DateTime? ClosedAt { get; set; }

    /// <summary>
    /// FK to the location where the session opened. Null for future manual
    /// adjustments.
    /// </summary>
    public Guid? OpeningLocationId { get; set; }

    /// <summary>
    /// FK to the location where the session closed.
    /// </summary>
    public Guid? ClosingLocationId { get; set; }

    /// <summary>
    /// Computed on close from <c>ClosedAt − OpenedAt</c> (V1: no break
    /// deduction).
    /// </summary>
    public int? WorkedMinutes { get; set; }

    /// <summary>
    /// Session lifecycle (Open / Closed; Abandoned reserved).
    /// </summary>
    public AttendanceSessionStatus Status { get; set; } = AttendanceSessionStatus.Open;
}
