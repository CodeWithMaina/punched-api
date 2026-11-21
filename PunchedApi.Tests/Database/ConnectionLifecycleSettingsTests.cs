using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Tests.Database;

/// <summary>
/// Guards the keepalive distinction. The scale-to-zero profile must not be able to
/// inherit Npgsql's application-level keepalive by accident, and the TCP-level
/// settings must stay a separate, explicit choice.
/// </summary>
public class ConnectionLifecycleSettingsTests
{
    [Fact]
    public void The_defaults_are_scale_to_zero_safe()
    {
        var settings = new ConnectionLifecycleSettings();

        Assert.Equal(0, settings.MinimumPoolSize);
        Assert.Equal(0, settings.KeepAliveSeconds);   // Npgsql's own default is 0 (disabled)
        Assert.False(settings.TcpKeepAlive);
        Assert.Equal(0, settings.TcpKeepAliveTimeSeconds);
        Assert.Equal(300, settings.ConnectionIdleLifetimeSeconds);
        Assert.Empty(settings.Validate(scaleToZeroFriendly: true));
    }

    [Fact]
    public void An_application_keepalive_is_rejected_in_the_scale_to_zero_profile()
    {
        var settings = new ConnectionLifecycleSettings { KeepAliveSeconds = 30 };

        var problems = settings.Validate(scaleToZeroFriendly: true);

        Assert.Contains(problems, problem =>
            problem.Contains("Database:KeepAliveSeconds") && problem.Contains("ScaleToZeroFriendlyMode"));
    }

    [Fact]
    public void An_application_keepalive_is_allowed_on_a_latency_first_deployment()
    {
        var settings = new ConnectionLifecycleSettings { KeepAliveSeconds = 30 };

        Assert.Empty(settings.Validate(scaleToZeroFriendly: false));
        Assert.Contains("appKeepalive=30s", settings.Describe());
    }

    [Fact]
    public void The_guard_has_an_explicit_escape_hatch_that_warns()
    {
        var settings = new ConnectionLifecycleSettings
        {
            KeepAliveSeconds = 30,
            AllowKeepAliveInScaleToZeroMode = true
        };

        Assert.Empty(settings.Validate(scaleToZeroFriendly: true));
        Assert.Contains(settings.Warnings(scaleToZeroFriendly: true),
            warning => warning.Contains("Verify on the Neon dashboard"));
    }

    [Fact]
    public void Tcp_keepalive_is_independent_of_the_application_keepalive()
    {
        var settings = new ConnectionLifecycleSettings
        {
            KeepAliveSeconds = 0,
            TcpKeepAlive = true,
            TcpKeepAliveTimeSeconds = 60,
            TcpKeepAliveIntervalSeconds = 15
        };

        // TCP keepalive is allowed with the application keepalive off...
        Assert.Empty(settings.Validate(scaleToZeroFriendly: true));
        // ...and is described separately.
        Assert.Contains("appKeepalive=off", settings.Describe());
        Assert.Contains("tcpKeepalive=on(time=60s,interval=15s)", settings.Describe());
    }

    [Fact]
    public void Tcp_keepalive_overrides_without_the_switch_are_rejected()
    {
        var settings = new ConnectionLifecycleSettings
        {
            TcpKeepAlive = false,
            TcpKeepAliveTimeSeconds = 60
        };

        Assert.Contains(settings.Validate(false), problem => problem.Contains("Database:TcpKeepAlive is false"));
    }

    [Fact]
    public void A_tcp_keepalive_interval_without_a_time_is_rejected()
    {
        var settings = new ConnectionLifecycleSettings
        {
            TcpKeepAlive = true,
            TcpKeepAliveTimeSeconds = 0,
            TcpKeepAliveIntervalSeconds = 30
        };

        Assert.Contains(settings.Validate(false), problem =>
            problem.Contains("TcpKeepAliveIntervalSeconds") && problem.Contains("TcpKeepAliveTimeSeconds"));
    }

    [Fact]
    public void A_minimum_pool_larger_than_the_maximum_is_rejected()
    {
        var settings = new ConnectionLifecycleSettings { MinimumPoolSize = 10, MaximumPoolSize = 4 };

        Assert.Contains(settings.Validate(false), problem => problem.Contains("must not exceed"));
    }

    [Fact]
    public void A_warm_minimum_pool_warns_in_scale_to_zero_mode()
    {
        var settings = new ConnectionLifecycleSettings { MinimumPoolSize = 2 };

        Assert.Empty(settings.Validate(scaleToZeroFriendly: true));
        Assert.Contains(settings.Warnings(scaleToZeroFriendly: true), warning => warning.Contains("MinimumPoolSize=0"));
    }

    [Fact]
    public void The_summary_contains_no_credentials()
    {
        var summary = new ConnectionLifecycleSettings { KeepAliveSeconds = 0 }.Describe();

        Assert.Contains("appKeepalive=off", summary);
        Assert.DoesNotContain("postgres", summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@", summary);
        Assert.DoesNotContain("password", summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Host=", summary, StringComparison.OrdinalIgnoreCase);
    }
}
