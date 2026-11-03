namespace PunchedApi.Tests;

/// <summary>
/// A clock a test can pin.
///
/// <para>Booking rules are time-relative — lead time, "the new time must be in the
/// future", the past-slot boundary, proposal expiry — so a fixture that derives its
/// appointment from <c>DateTime.UtcNow</c> only passes at certain hours. The
/// reschedule tests did exactly that: they booked <c>UtcNow + 2h</c> against an
/// 08:00–20:00 shift, so they passed in the morning and failed from about 18:00
/// onwards, when <c>UtcNow + 2h</c> fell outside the shift.</para>
///
/// <para>Injecting <see cref="TimeProvider"/> (BCL, no new dependency) lets those
/// tests pin "now" to a fixed instant and use fixed dates, so the rule under test
/// is exercised the same way at any hour, in any timezone, on any day.</para>
/// </summary>
internal sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now;

    public TestClock(DateTimeOffset now) => _now = now;

    /// <summary>A clock fixed at the given UTC wall-clock instant.</summary>
    public static TestClock AtUtc(int year, int month, int day, int hour = 0, int minute = 0) =>
        new(new DateTimeOffset(new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Utc)));

    public override DateTimeOffset GetUtcNow() => _now;

    /// <summary>Moves the clock forward (used by expiry tests).</summary>
    public void Advance(TimeSpan by) => _now = _now.Add(by);
}
