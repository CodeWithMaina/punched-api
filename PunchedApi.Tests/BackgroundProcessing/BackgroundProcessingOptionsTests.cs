using Microsoft.Extensions.Configuration;
using PunchedApi.Application.BackgroundProcessing;

namespace PunchedApi.Tests.BackgroundProcessing;

public class BackgroundProcessingOptionsTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(pair =>
                new KeyValuePair<string, string?>(pair.Key, pair.Value)))
            .Build();

    [Fact]
    public void Shared_values_apply_to_every_worker()
    {
        var options = new BackgroundProcessingOptions(Config(
            ("BackgroundProcessing:MaxIdleIntervalSeconds", "45"),
            ("BackgroundProcessing:FirstIdleIntervalSeconds", "10")));

        Assert.Equal(45, options.For("cleanup").MaxIdleIntervalSeconds);
        Assert.Equal(45, options.For("notifications").MaxIdleIntervalSeconds);
        Assert.Equal(10, options.For("notifications").FirstIdleIntervalSeconds);
    }

    [Fact]
    public void A_worker_section_overrides_only_the_keys_it_names()
    {
        var options = new BackgroundProcessingOptions(Config(
            ("BackgroundProcessing:MaxIdleIntervalSeconds", "45"),
            ("BackgroundProcessing:JitterRatio", "0.25"),
            ("BackgroundProcessing:Workers:notifications:MaxIdleIntervalSeconds", "15"),
            ("BackgroundProcessing:Workers:notifications:BatchSize", "100")));

        var notifications = options.For("notifications");
        var other = options.For("analytics");

        Assert.Equal(15, notifications.MaxIdleIntervalSeconds);
        Assert.Equal(100, notifications.BatchSize);
        Assert.Equal(0.25, notifications.JitterRatio);       // inherited
        Assert.Equal(45, other.MaxIdleIntervalSeconds);      // untouched
        Assert.Null(other.BatchSize);
    }

    [Fact]
    public void Scale_to_zero_mode_is_a_root_switch_not_a_per_worker_one()
    {
        var options = new BackgroundProcessingOptions(Config(
            ("BackgroundProcessing:ScaleToZeroFriendlyMode", "true"),
            ("BackgroundProcessing:Workers:notifications:MaxIdleIntervalSeconds", "15")));

        Assert.True(options.For("notifications").ScaleToZeroFriendly);
        Assert.True(options.For("cleanup").ScaleToZeroFriendly);
        Assert.Equal(360, options.For("notifications").EffectiveMaxIdleIntervalSeconds);
    }

    [Fact]
    public void Missing_configuration_falls_back_to_code_defaults()
    {
        var options = new BackgroundProcessingOptions(Config());

        Assert.Equal(2, options.For("notifications").ActiveIntervalSeconds);
        Assert.False(options.For("notifications").ScaleToZeroFriendly);
        Assert.Empty(options.Validate());
    }

    [Fact]
    public void A_floor_inside_neon_suspend_window_is_rejected()
    {
        var options = new BackgroundProcessingOptions(Config(
            ("BackgroundProcessing:ScaleToZeroFriendlyMode", "true"),
            ("BackgroundProcessing:ScaleToZeroIdleFloorSeconds", "300")));

        var problems = options.Validate();

        Assert.NotEmpty(problems);
        Assert.Contains(problems, problem => problem.Contains("5-minute idle window"));
    }

    [Fact]
    public void A_nonsensical_ramp_is_rejected()
    {
        var options = new BackgroundProcessingOptions(Config(
            ("BackgroundProcessing:ActiveIntervalSeconds", "30"),
            ("BackgroundProcessing:FirstIdleIntervalSeconds", "10")));

        var problems = options.Validate();

        Assert.Contains(problems, problem => problem.Contains("FirstIdleIntervalSeconds"));
    }

    [Fact]
    public void The_summary_lists_every_worker_without_secrets()
    {
        var options = new BackgroundProcessingOptions(Config(
            ("BackgroundProcessing:Workers:media-cleanup:Enabled", "false")));

        var lines = options.DescribeLines();

        Assert.Contains(lines, line => line.StartsWith("Scale-to-zero-friendly mode"));
        Assert.Contains(lines, line => line.StartsWith("media-cleanup") && line.Contains("disabled"));
        Assert.Contains(lines, line => line.StartsWith("notifications"));
        Assert.DoesNotContain(lines, line => line.Contains("postgres://", StringComparison.OrdinalIgnoreCase));
    }
}
