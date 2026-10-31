namespace PunchedApi.Application.Services;

/// <summary>
/// The authoritative appointment status vocabulary.
///
/// <para>Five statuses exist. <c>draft</c> is deliberately absent: the platform
/// has no server-side draft concept (a booking is either created or not), so
/// keeping the status would mean maintaining a lifecycle nothing produces.
/// <c>pending</c> is absent because a pending <i>reschedule</i> is a separate
/// workflow (<c>RescheduleRequest</c>), never an appointment status, and
/// <c>in_progress</c> is absent because nothing in the domain observes service
/// start.</para>
/// </summary>
public static class AppointmentStatuses
{
    /// <summary>Created and awaiting staff confirmation. The initial status of every booking.</summary>
    public const string Booked = "booked";

    /// <summary>The business has acknowledged the booking.</summary>
    public const string Confirmed = "confirmed";

    /// <summary>Terminal: the customer attended and the service was delivered.</summary>
    public const string Completed = "completed";

    /// <summary>Terminal: the appointment was called off before it happened.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>Terminal: the customer did not attend.</summary>
    public const string NoShow = "no_show";

    /// <summary>Every status the backend can produce, in lifecycle order.</summary>
    public static readonly IReadOnlyList<string> All = new[] { Booked, Confirmed, Completed, Cancelled, NoShow };

    /// <summary>Statuses that end the lifecycle — nothing may follow them.</summary>
    public static readonly IReadOnlyList<string> Terminal = new[] { Completed, Cancelled, NoShow };

    /// <summary>Is this a status the backend recognises?</summary>
    public static bool IsKnown(string? status) =>
        !string.IsNullOrWhiteSpace(status) && All.Contains(status, StringComparer.Ordinal);

    /// <summary>Is this status terminal?</summary>
    public static bool IsTerminal(string? status) =>
        !string.IsNullOrWhiteSpace(status) && Terminal.Contains(status, StringComparer.Ordinal);
}

/// <summary>
/// <b>Server-side appointment state machine.</b> Every status change is
/// validated here before anything else happens, so no controller, background
/// job or future caller can put an appointment into an impossible state.
///
/// <para>Transition table (from → to, and the permission the actor needs):</para>
/// <code>
/// booked    → confirmed   appointments.confirm   (business/staff)
/// booked    → cancelled   appointments.cancel    (business/staff/owning customer)
/// booked    → no_show     appointments.no_show    (business/staff)
/// confirmed → completed   appointments.complete  (business/staff)
/// confirmed → cancelled   appointments.cancel    (business/staff)
/// confirmed → no_show     appointments.no_show    (business/staff)
/// completed → (terminal — no transitions)
/// cancelled → (terminal — no transitions)
/// no_show   → (terminal — no transitions)
/// </code>
///
/// <para><b>Repeating a transition is not an error.</b> A transition to the
/// status an appointment already has is reported as an idempotent no-op by
/// <see cref="AppointmentService"/>, so a retried request never writes a second
/// history row, fires a second notification or accrues a second commission.</para>
///
/// <para><b>Correction workflow.</b> A completed/cancelled/no-show appointment
/// cannot be moved by any transition. Reopening a terminal appointment is an
/// explicit correction workflow the platform does not offer, so it is refused
/// rather than silently allowed.</para>
/// </summary>
public static class AppointmentStatusMachine
{
    private static readonly Dictionary<string, string[]> Allowed = new(StringComparer.Ordinal)
    {
        [AppointmentStatuses.Booked] = new[]
        {
            AppointmentStatuses.Confirmed,
            AppointmentStatuses.Cancelled,
            AppointmentStatuses.NoShow
        },
        [AppointmentStatuses.Confirmed] = new[]
        {
            AppointmentStatuses.Completed,
            AppointmentStatuses.Cancelled,
            AppointmentStatuses.NoShow
        },
        [AppointmentStatuses.Completed] = Array.Empty<string>(),
        [AppointmentStatuses.Cancelled] = Array.Empty<string>(),
        [AppointmentStatuses.NoShow] = Array.Empty<string>()
    };

    /// <summary>The permission code required to enter the given status, or null.</summary>
    public static string? RequiredPermission(string toStatus) => toStatus switch
    {
        AppointmentStatuses.Confirmed => "appointments.confirm",
        AppointmentStatuses.Completed => "appointments.complete",
        AppointmentStatuses.Cancelled => "appointments.cancel",
        AppointmentStatuses.NoShow => "appointments.no_show",
        _ => null
    };

    /// <summary>Is the from → to transition permitted by the lifecycle?</summary>
    public static bool CanTransition(string? from, string? to)
    {
        if (!AppointmentStatuses.IsKnown(from) || !AppointmentStatuses.IsKnown(to)) return false;
        if (string.Equals(from, to, StringComparison.Ordinal)) return true; // idempotent repeat
        return Allowed.TryGetValue(from!, out var targets) && targets.Contains(to!, StringComparer.Ordinal);
    }

    /// <summary>Every status reachable from the given one (excluding itself).</summary>
    public static IReadOnlyList<string> AllowedFrom(string? status) =>
        AppointmentStatuses.IsKnown(status) && Allowed.TryGetValue(status!, out var targets)
            ? targets
            : Array.Empty<string>();

    /// <summary>
    /// Human-readable refusal, naming what IS possible, so an API consumer can
    /// act on the error instead of guessing.
    /// </summary>
    public static string DescribeRefusal(string from, string to)
    {
        if (!AppointmentStatuses.IsKnown(from))
            return $"Appointment has unknown status '{from}'.";

        if (AppointmentStatuses.IsTerminal(from))
            return $"Appointment is '{from}' (a terminal state) and cannot be changed to '{to}'.";

        var allowed = AllowedFrom(from);
        return allowed.Count == 0
            ? $"Cannot transition from '{from}' to '{to}'."
            : $"Cannot transition from '{from}' to '{to}'. Allowed from '{from}': {string.Join(", ", allowed)}.";
    }
}

