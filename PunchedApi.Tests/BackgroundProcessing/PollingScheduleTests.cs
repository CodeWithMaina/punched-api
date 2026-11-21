using PunchedApi.Application.BackgroundProcessing;

namespace PunchedApi.Tests.BackgroundProcessing;

public class PollingScheduleTests
{
    private static PollingProfile Profile(Action<PollingProfile>? configure = null)
    {
        var profile = new PollingProfile
        {
            ActiveIntervalSeconds = 2,
            FirstIdleIntervalSeconds = 10,
            MaxIdleIntervalSeconds = 60,
            BackoffMultiplier = 2,
            JitterRatio = 0,
            MaxIdleDurationSeconds = 0
        };
        configure?.Invoke(profile);
        return profile;
    }

    [Fact]
    public void Idle_backoff_escalates_and_caps()
    {
        var schedule = new PollingSchedule(Profile());

        Assert.Equal(TimeSpan.FromSeconds(10), schedule.Next(workFound: false));
        Assert.Equal(TimeSpan.FromSeconds(20), schedule.Next(workFound: false));
        Assert.Equal(TimeSpan.FromSeconds(40), schedule.Next(workFound: false));
        Assert.Equal(TimeSpan.FromSeconds(60), schedule.Next(workFound: false));
        Assert.Equal(TimeSpan.FromSeconds(60), schedule.Next(workFound: false));
    }

    [Fact]
    public void Work_found_resets_the_ramp_to_the_active_interval()
    {
        var schedule = new PollingSchedule(Profile());
        schedule.Next(false);
        schedule.Next(false);
        schedule.Next(false);

        Assert.Equal(TimeSpan.FromSeconds(2), schedule.Next(workFound: true));
        Assert.Equal(0, schedule.ConsecutiveEmptyPolls);

        // The next empty poll starts the ramp again rather than resuming deep idle.
        Assert.Equal(TimeSpan.FromSeconds(10), schedule.Next(workFound: false));
    }

    [Fact]
    public void Deep_idle_kicks_in_after_max_idle_duration()
    {
        var schedule = new PollingSchedule(Profile(p =>
        {
            p.FirstIdleIntervalSeconds = 10;
            p.MaxIdleIntervalSeconds = 60;
            p.BackoffMultiplier = 2;
            p.MaxIdleDurationSeconds = 15;
        }));

        Assert.Equal(TimeSpan.FromSeconds(10), schedule.Next(false)); // elapsed 0 -> 10
        Assert.Equal(TimeSpan.FromSeconds(20), schedule.Next(false)); // elapsed 10 < 15 -> 20
        Assert.Equal(TimeSpan.FromSeconds(60), schedule.Next(false)); // elapsed 30 >= 15 -> ceiling
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(0.999999)]
    public void Jitter_stays_inside_the_configured_band(double randomValue)
    {
        var schedule = new PollingSchedule(
            Profile(p => p.JitterRatio = 0.2),
            () => randomValue);

        var delay = schedule.Next(workFound: false);

        Assert.InRange(delay.TotalSeconds, 10 * 0.8, 10 * 1.2);
    }

    [Fact]
    public void Scale_to_zero_mode_never_drops_below_the_floor()
    {
        var schedule = new PollingSchedule(
            Profile(p =>
            {
                p.ScaleToZeroFriendly = true;
                p.ScaleToZeroIdleFloorSeconds = 360;
                p.MaxIdleIntervalSeconds = 30;
                p.JitterRatio = 0.2;
            }),
            () => 0.0); // worst case for a symmetric jitter

        var maxIdle = Profile(p => { p.ScaleToZeroFriendly = true; p.ScaleToZeroIdleFloorSeconds = 360; p.MaxIdleIntervalSeconds = 30; })
            .EffectiveMaxIdleIntervalSeconds;

        for (var i = 0; i < 6; i++)
        {
            var delay = schedule.Next(workFound: false);
            Assert.True(
                delay.TotalSeconds >= 360,
                $"idle delay {delay.TotalSeconds}s must stay above Neon's 5-minute suspend window");
            Assert.True(delay.TotalSeconds <= maxIdle * 1.2);
        }
    }

    [Fact]
    public void Scale_to_zero_mode_raises_the_ceiling_to_the_floor()
    {
        var profile = Profile(p =>
        {
            p.ScaleToZeroFriendly = true;
            p.ScaleToZeroIdleFloorSeconds = 360;
            p.MaxIdleIntervalSeconds = 15;
        });

        Assert.Equal(360, profile.EffectiveMaxIdleIntervalSeconds);
    }

    [Fact]
    public void Scale_to_zero_ceiling_is_quantised_to_whole_ticks_without_lengthening_it()
    {
        // 900 s must land on the 360 s lattice. Rounding down to 720 s keeps the
        // worker inside its documented maximum delay; rounding up to 1080 s would
        // silently stretch a 15-minute guarantee to 18 minutes.
        var profile = Profile(p =>
        {
            p.ScaleToZeroFriendly = true;
            p.ScaleToZeroIdleFloorSeconds = 360;
            p.MaxIdleIntervalSeconds = 900;
        });

        Assert.Equal(720, profile.EffectiveMaxIdleIntervalSeconds);
    }

    [Fact]
    public void Wake_keeps_the_worker_near_the_first_idle_interval()
    {
        var schedule = new PollingSchedule(Profile());
        schedule.Next(false);
        schedule.Next(false);

        var delay = schedule.NextAfterWake();

        Assert.Equal(TimeSpan.FromSeconds(10), delay);
        Assert.Equal(0, schedule.ConsecutiveEmptyPolls);
    }

    [Fact]
    public void Latency_first_mode_keeps_the_operator_ceiling()
    {
        var profile = Profile(p =>
        {
            p.ScaleToZeroFriendly = false;
            p.MaxIdleIntervalSeconds = 15;
        });

        Assert.Equal(15, profile.EffectiveMaxIdleIntervalSeconds);
    }
}
