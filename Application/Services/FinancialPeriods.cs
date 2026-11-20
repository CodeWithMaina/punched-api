namespace PunchedApi.Application.Services;

/// <summary>
/// The date windows every financial figure is calculated over — resolved ONCE, in the
/// business's own timezone, so the overview, the cashbook and the analytics can never
/// disagree about what "last month" means.
///
/// <para><b>Why the timezone matters.</b> "Today's takings" for a Nairobi salon means the
/// Nairobi day. Computing it in UTC would move every transaction made after 21:00 into the
/// following day, which is exactly the kind of quiet inaccuracy a ledger must not have.
/// The window is therefore built from the business's local midnight and then converted
/// back to the UTC instants the database stores.</para>
/// </summary>
public sealed record FinancialPeriod(string Key, DateTime FromUtc, DateTime ToUtc)
{
    /// <summary>today | yesterday | last_7_days | last_30_days | this_month | last_month | this_year | custom.</summary>
    public const string Today = "today";
    public const string Yesterday = "yesterday";
    public const string Last7Days = "last_7_days";
    public const string Last30Days = "last_30_days";
    public const string ThisMonth = "this_month";
    public const string LastMonth = "last_month";
    public const string ThisYear = "this_year";
    public const string Custom = "custom";

    /// <summary>How long the window is, in days (used to choose a trend bucket size).</summary>
    public double Days => (ToUtc - FromUtc).TotalDays;

    /// <summary>Trend bucket for this window: <c>day</c>, <c>week</c> or <c>month</c>.</summary>
    public string Granularity => Days switch
    {
        <= 62 => "day",
        <= 200 => "week",
        _ => "month"
    };

    /// <summary>
    /// Builds a window from the caller's period key. Unknown keys fall back to the last 30
    /// days (never to "all time": an accidental all-time sum would be far more misleading).
    /// </summary>
    public static FinancialPeriod Resolve(
        string? key,
        DateTime? from = null,
        DateTime? to = null,
        TimeZoneInfo? timeZone = null)
    {
        var tz = timeZone ?? TimeZoneInfo.Utc;
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        var today = localNow.Date;

        var normalised = (key ?? string.Empty).Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');

        DateTime startLocal;
        DateTime endLocal;

        switch (normalised)
        {
            case Today:
                startLocal = today;
                endLocal = today.AddDays(1);
                break;

            case Yesterday:
                startLocal = today.AddDays(-1);
                endLocal = today;
                break;

            case Last7Days:
                startLocal = today.AddDays(-6);
                endLocal = today.AddDays(1);
                break;

            case ThisMonth:
                startLocal = new DateTime(today.Year, today.Month, 1);
                endLocal = startLocal.AddMonths(1);
                break;

            case LastMonth:
                startLocal = new DateTime(today.Year, today.Month, 1).AddMonths(-1);
                endLocal = new DateTime(today.Year, today.Month, 1);
                break;

            case ThisYear:
                startLocal = new DateTime(today.Year, 1, 1);
                endLocal = startLocal.AddYears(1);
                break;

            case Custom:
                {
                    // A custom range is expressed in LOCAL dates (what the owner picked in the
                    // date picker). The end date is inclusive, so the window ends at the next
                    // local midnight. A missing or inverted range degrades to the last 30 days
                    // rather than silently returning nothing.
                    if (!from.HasValue || !to.HasValue || to.Value.Date < from.Value.Date)
                    {
                        startLocal = today.AddDays(-29);
                        endLocal = today.AddDays(1);
                        normalised = Last30Days;
                        break;
                    }

                    startLocal = from.Value.Date;
                    endLocal = to.Value.Date.AddDays(1);
                    break;
                }

            default:
                normalised = Last30Days;
                startLocal = today.AddDays(-29);
                endLocal = today.AddDays(1);
                break;
        }

        return new FinancialPeriod(
            normalised,
            ToUtcInstant(startLocal, tz),
            ToUtcInstant(endLocal, tz));
    }

    /// <summary>
    /// Converts a local wall-clock instant to UTC, tolerating a spring-forward gap (an hour
    /// that does not exist locally) by advancing to the first valid instant instead of
    /// throwing and failing the whole report. An ambiguous hour (autumn fall-back) resolves
    /// to the standard-time offset, which is a one-hour difference at one midnight a year —
    /// far preferable to refusing to report.
    /// </summary>
    private static DateTime ToUtcInstant(DateTime local, TimeZoneInfo tz)
    {
        if (tz.IsInvalidTime(local))
            local = local.AddHours(1);

        return TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(local, DateTimeKind.Unspecified), tz);
    }
}
