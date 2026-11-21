using PunchedApi.Application.BackgroundProcessing;

namespace PunchedApi.Tests.BackgroundProcessing;

public class PollingSharedTickTests
{
    private static readonly DateTimeOffset Origin = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Delay_points_at_the_next_tick()
    {
        var delay = PollingSharedTick.DelayUntilNextTick(
            Origin.AddSeconds(100), Origin, periodSeconds: 360, jitterRatio: 0, workerName: "notifications");

        Assert.Equal(260, delay.TotalSeconds);
    }

    [Fact]
    public void Landing_exactly_on_a_tick_waits_a_whole_period_instead_of_busy_looping()
    {
        var delay = PollingSharedTick.DelayUntilNextTick(
            Origin.AddSeconds(720), Origin, periodSeconds: 360, jitterRatio: 0, workerName: "notifications");

        Assert.Equal(360, delay.TotalSeconds);
    }

    [Fact]
    public void Spread_is_bounded_and_deterministic()
    {
        var now = Origin.AddSeconds(10);

        var first = PollingSharedTick.DelayUntilNextTick(now, Origin, 360, 0.15, "cleanup");
        var second = PollingSharedTick.DelayUntilNextTick(now, Origin, 360, 0.15, "cleanup");

        Assert.Equal(first, second);
        Assert.InRange(first.TotalSeconds, 350, 355);
    }

    [Fact]
    public void Different_workers_do_not_all_share_the_same_millisecond()
    {
        var now = Origin.AddSeconds(10);
        string[] workers = { "notifications", "cleanup", "analytics", "domain-events", "payouts", "media-processing" };

        var delays = workers
            .Select(name => PollingSharedTick.DelayUntilNextTick(now, Origin, 360, 0.15, name).TotalSeconds)
            .Distinct()
            .Count();

        Assert.True(delays >= 2, "the deterministic spread should de-synchronise the idle workers");
    }

    [Theory]
    [InlineData(300, 360, 360)]
    [InlineData(900, 360, 1080)]
    [InlineData(3600, 360, 3600)]
    [InlineData(86400, 360, 86400)]
    [InlineData(10, 360, 360)]
    public void Periods_round_up_to_whole_ticks(int configured, int tick, int expected)
    {
        Assert.Equal(expected, PollingSharedTick.AlignPeriodSeconds(configured, tick));
    }

    [Fact]
    public void A_period_shorter_than_the_minimum_tick_still_produces_a_usable_period()
    {
        Assert.Equal(60, PollingSharedTick.AlignPeriodSeconds(5, 5));
    }
}
