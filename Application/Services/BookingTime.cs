namespace PunchedApi.Application.Services;

/// <summary>
/// Timezone resolution shared by the booking read/write paths, so "the business's day" is
/// computed the same way everywhere (IANA or Windows ids, UTC when unknown).
/// </summary>
public static class BookingTime
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, TimeZoneInfo> Cache = new();

    public static TimeZoneInfo ResolveTimeZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return TimeZoneInfo.Utc;

        return Cache.GetOrAdd(id, key =>
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(key);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                return TimeZoneInfo.Utc;
            }
        });
    }

    /// <summary>Converts a business-local date and time to UTC, tolerating DST gaps.</summary>
    public static DateTime ToUtc(DateTime local, TimeZoneInfo tz)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (tz.IsInvalidTime(unspecified)) unspecified = unspecified.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(unspecified, tz);
    }
}
