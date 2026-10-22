namespace PunchedApi.Application.Scheduling;

/// <summary>
/// Pure math for repeatable win-back cycles.
/// </summary>
/// <remarks>
/// A cycle is keyed by <c>(activity anchor, cycle index)</c> so that:
/// <list type="bullet">
/// <item>repeated scans inside the same window produce the same dedupe key
/// (idempotent inserts), and</item>
/// <item>a customer who returns (new appointment) gets a fresh anchor, so a
/// later lapse can never collide with the previous cycle's key.</item>
/// </list>
/// Keys stay well below the 200-char limit of
/// <c>scheduled_jobs.dedupe_key</c> (two GUIDs + ticks + cycle index).
/// </remarks>
public static class WinBackCyclePolicy
{
    public const string JobType = "customer.lapsed";

    /// <summary>
    /// Notification types that count as a win-back nudge for cooldown purposes.
    /// Both are registered in <c>NotificationTypes</c> so both render.
    /// </summary>
    public static readonly string[] WinBackTemplateTypes =
    {
        "customer.lapsed",
        "loyalty.win_back_nudge"
    };

    /// <summary>Appointment statuses that do not represent customer activity.</summary>
    private static readonly string[] NonActivityStatuses = { "cancelled", "no_show" };

    public static bool CountsAsActivity(string? appointmentStatus) =>
        string.IsNullOrWhiteSpace(appointmentStatus)
        || !NonActivityStatuses.Contains(appointmentStatus, StringComparer.OrdinalIgnoreCase);

    /// <summary>Prefix shared by every cycle of one (customer, business) pair.</summary>
    public static string DedupeKeyPrefix(Guid customerId, Guid businessId) =>
        $"{JobType}:{customerId:D}:{businessId:D}:";

    /// <summary>Stable per-(anchor, cycle) key; see class remarks.</summary>
    public static string BuildDedupeKey(
        Guid customerId, Guid businessId, DateTime anchorUtc, int cycleIndex)
    {
        if (cycleIndex < 0) throw new ArgumentOutOfRangeException(nameof(cycleIndex));
        // Timestamps read from PostgreSQL have Kind=Unspecified; ToUniversalTime()
        // would treat them as local time and shift the ticks. Normalise instead.
        var anchor = anchorUtc.Kind == DateTimeKind.Utc
            ? anchorUtc
            : DateTime.SpecifyKind(anchorUtc, DateTimeKind.Utc);
        return $"{DedupeKeyPrefix(customerId, businessId)}{anchor.Ticks:D}:c{cycleIndex}";
    }

    /// <summary>Whole cooldown windows elapsed since the anchor (0 when not yet elapsed).</summary>
    public static int ComputeCycleIndex(DateTime anchorUtc, DateTime nowUtc, int cooldownDays)
    {
        if (cooldownDays < 1) throw new ArgumentOutOfRangeException(nameof(cooldownDays));
        var elapsed = nowUtc - anchorUtc;
        if (elapsed <= TimeSpan.Zero) return 0;
        return (int)Math.Floor(elapsed.TotalDays / cooldownDays);
    }

    public static bool IsLapsed(DateTime anchorUtc, DateTime nowUtc, int lapseInactivityDays) =>
        nowUtc - anchorUtc >= TimeSpan.FromDays(Math.Max(1, lapseInactivityDays));

    /// <summary>
    /// Advisory-lock key that serialises win-back dispatch per (business, customer)
    /// across API instances. The hash is computed in PostgreSQL, not here.
    /// </summary>
    public static string LockKey(Guid customerId, Guid businessId) =>
        $"winback:{businessId:D}:{customerId:D}";
}
