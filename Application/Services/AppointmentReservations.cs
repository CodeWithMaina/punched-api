using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

/// <summary>
/// <b>Does an appointment reserve its staff member's time?</b> One authority for
/// the rule, used by every availability read and every booking write.
///
/// <para>Before this existed the rule was "anything except cancelled", repeated in
/// five places (the calendar query, the slot resolver, the two pre-insert checks
/// and the PostgreSQL exclusion constraint). Five copies of a scheduling rule is
/// five chances to disagree, and they did: a <i>terminal</i> appointment —
/// <c>completed</c> or <c>no_show</c>, which <see cref="AppointmentStatuses.Terminal"/>
/// defines as the end of the lifecycle — kept blocking its staff member's interval
/// even though the appointment no longer occupies any time.</para>
///
/// <para>Only the two live statuses reserve time:</para>
/// <list type="bullet">
///   <item><description><c>booked</c> — the initial status of every booking, so a
///   pending-but-not-yet-confirmed appointment is a real reservation.</description></item>
///   <item><description><c>confirmed</c> — the business has acknowledged it.</description></item>
/// </list>
/// <para><c>cancelled</c>, <c>completed</c> and <c>no_show</c> release the interval:
/// a cancelled booking never happened, and a completed or missed one is history
/// whose time has passed. Releasing on terminal statuses is also what makes a
/// reschedule's old interval free the moment it moves.</para>
/// </summary>
public static class AppointmentReservations
{
    /// <summary>The statuses that hold a staff member's time.</summary>
    public static readonly IReadOnlyList<string> ReservingStatuses = new[]
    {
        AppointmentStatuses.Booked,
        AppointmentStatuses.Confirmed
    };

    /// <summary>Does an appointment in this status reserve staff time?</summary>
    public static bool ReservesStaffTime(string? status) =>
        !string.IsNullOrWhiteSpace(status) &&
        ReservingStatuses.Contains(status, StringComparer.Ordinal);

    /// <summary>
    /// The SQL predicate the database-level exclusion constraint enforces. Kept
    /// here so the constraint and the application can never drift: the migration
    /// that (re)creates <c>appointments_no_staff_overlap</c> uses exactly this
    /// text.
    /// </summary>
    public const string ReservationPredicateSql = "status IN ('booked', 'confirmed')";

    /// <summary>
    /// Half-open interval overlap: <c>existingStart &lt; requestedEnd &amp;&amp;
    /// existingEnd &gt; requestedStart</c>.
    ///
    /// <para>Half-open is what makes back-to-back appointments legal — a booking
    /// ending at 15:00 does not block one starting at 15:00 — while still catching
    /// every partial overlap (starting inside, ending inside, containing, contained
    /// and identical intervals).</para>
    /// </summary>
    public static bool Overlaps(
        DateTime existingStart, DateTime existingEnd, DateTime requestedStart, DateTime requestedEnd) =>
        existingStart < requestedEnd && existingEnd > requestedStart;
}

/// <summary>
/// Serialises concurrent booking attempts for one staff member.
///
/// <para>Checking availability and then inserting is not enough on its own: two
/// requests can both pass the check and both insert. The database exclusion
/// constraint is the final backstop, but relying on it alone means the loser's
/// outcome depends on which constraint violation surfaces — a deadlock or a
/// serialization failure at the GiST index is reported differently from an
/// exclusion violation, which is how a genuine conflict used to reach the client
/// as <c>BOOKING_FAILED</c> (HTTP 400) instead of a conflict.</para>
///
/// <para>So the write paths take a PostgreSQL <b>transaction-scoped advisory
/// lock</b> keyed on (business, staff member) before re-checking availability.
/// The lock is released when the transaction ends, is scoped to one staff member
/// so unrelated bookings never contend, and makes the loser wait for the winner's
/// commit — after which the loser's own re-check sees the committed row and
/// returns a clean conflict.</para>
///
/// <para>Non-relational providers (the SQLite test harness) have no advisory
/// locks; there the call is a no-op, which is why the concurrency tests that
/// depend on database behaviour run against PostgreSQL.</para>
/// </summary>
public static class StaffTimeLock
{
    /// <summary>Lock namespace, so these locks can never collide with another feature's.</summary>
    private const string LockNamespace = "punched.appointment.staff";

    /// <summary>
    /// Takes the staff member's booking lock for the current transaction. Call it
    /// inside an open transaction and before the availability re-check.
    /// </summary>
    public static async Task AcquireAsync(
        ApplicationDbContext context,
        Guid businessId,
        Guid? staffUserId,
        CancellationToken cancellationToken = default)
    {
        // An unassigned appointment (no staff member) cannot conflict with anybody,
        // so there is nothing to serialise.
        if (staffUserId is not Guid staffId || staffId == Guid.Empty) return;
        if (!IsPostgres(context)) return;

        // Two-key form: (namespace, business:staff). Both keys are hashtext'd to
        // int4, which keeps the key space stable across processes and hosts.
        await context.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock(hashtext({0}), hashtext({1}))",
            new object[] { LockNamespace, $"{businessId:N}:{staffId:N}" },
            cancellationToken);
    }

    /// <summary>
    /// True when the provider enforces the exclusion constraint and supports
    /// advisory locks. Mirrors the provider check in the media module's row lock.
    /// </summary>
    public static bool IsPostgres(ApplicationDbContext context)
    {
        var provider = context.Database.ProviderName;
        return provider != null && provider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Turns a failed booking write into the one conflict response the API promises.
///
/// <para>Only a genuinely contended slot becomes <c>STAFF_TIME_UNAVAILABLE</c>
/// (HTTP 409). Anything else is an unexpected failure and is reported as such —
/// the distinction matters because the client's next action differs: pick another
/// time, versus try again.</para>
/// </summary>
public static class AppointmentConflicts
{
    /// <summary>
    /// The conflict code every booking entry point returns when the staff member's
    /// interval is no longer free.
    /// </summary>
    public const string StaffTimeUnavailable = "STAFF_TIME_UNAVAILABLE";

    /// <summary>Human-readable copy for that conflict.</summary>
    public const string StaffTimeUnavailableMessage =
        "That time is no longer available for this staff member. Please pick another slot.";

    /// <summary>
    /// True when the exception means "somebody else took the slot", rather than a
    /// transient database fault:
    /// <list type="bullet">
    ///   <item><description><c>23P01</c> — the exclusion constraint fired (the
    ///   backstop caught a race that slipped past the lock).</description></item>
    ///   <item><description><c>40001</c>/<c>40P01</c> — serialization failure or
    ///   deadlock while contending for the same staff member's interval; retrying
    ///   would simply find the slot taken.</description></item>
    /// </list>
    /// </summary>
    public static bool IsSlotConflict(Exception exception) =>
        FindPostgres(exception) is { } pg &&
        (pg.SqlState == PostgresErrorCodes.ExclusionViolation ||
         pg.SqlState == PostgresErrorCodes.SerializationFailure ||
         pg.SqlState == PostgresErrorCodes.DeadlockDetected);

    /// <summary>The Npgsql exception inside any wrapper (EF wraps in DbUpdateException).</summary>
    private static PostgresException? FindPostgres(Exception? exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
            if (current is PostgresException pg) return pg;
        return null;
    }

    /// <summary>
    /// Logs a contended slot at information level: it is an expected outcome of two
    /// customers wanting the same time, not a fault worth an error log.
    /// </summary>
    public static void LogConflict(ILogger logger, Exception exception, Guid businessId, Guid? staffUserId)
    {
        var pg = FindPostgres(exception);
        logger.LogInformation(
            "Booking conflict for staff {StaffUserId} at business {BusinessId} (SQLSTATE {SqlState}); the interval was already reserved.",
            staffUserId, businessId, pg?.SqlState ?? "unknown");
    }
}
