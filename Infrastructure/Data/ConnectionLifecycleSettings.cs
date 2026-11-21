namespace PunchedApi.Infrastructure.Data;

/// <summary>
/// The Npgsql connection lifecycle, bound from the <c>Database</c> configuration
/// section and validated before a single connection is opened.
///
/// <para><b>Two different keepalives, deliberately separated.</b></para>
/// <list type="bullet">
///   <item><b>Application-level</b> (<see cref="KeepAliveSeconds"/> →
///   Npgsql <c>KeepAlive</c>): Npgsql itself acts on an otherwise idle connection —
///   its documented behaviour is "the number of seconds of connection inactivity
///   before Npgsql sends a keepalive query". Npgsql's own default is 0 (disabled).
///   On a serverless database this is traffic the database never asked for, so the
///   scale-to-zero profile forbids it.</item>
///   <item><b>TCP-level</b> (<see cref="TcpKeepAlive"/>, <see cref="TcpKeepAliveTimeSeconds"/>,
///   <see cref="TcpKeepAliveIntervalSeconds"/>): operating-system keepalive on the
///   socket. It exists to survive a NAT/proxy silently dropping an idle mapping and
///   is an explicit opt-in; it is not a substitute for the application-level setting
///   and is never enabled implicitly.</item>
/// </list>
///
/// <para><b>Why the guard exists.</b> A non-zero application-level keepalive is the
/// single easiest way to keep a Neon Free compute awake around the clock without a
/// single application query. The deployment profile that depends on suspension
/// (<c>BackgroundProcessing:ScaleToZeroFriendlyMode=true</c>) therefore refuses to
/// start with one configured, unless an operator explicitly overrides the guard.</para>
///
/// <para>Pure and dependency-free so every rule is unit-testable.</para>
/// </summary>
public sealed class ConnectionLifecycleSettings
{
    public const string SectionName = "Database";

    /// <summary>Minimum connections kept in the pool. 0 lets the pool drain when idle.</summary>
    public int MinimumPoolSize { get; set; }

    /// <summary>Maximum connections this process may open.</summary>
    public int MaximumPoolSize { get; set; } = 8;

    /// <summary>
    /// Npgsql application-level keepalive, in seconds. 0 = disabled (Npgsql's own
    /// default). Must stay 0 whenever the deployment relies on database suspension.
    /// </summary>
    public int KeepAliveSeconds { get; set; }

    /// <summary>Opt into operating-system TCP keepalive.</summary>
    public bool TcpKeepAlive { get; set; }

    /// <summary>Seconds of TCP inactivity before an OS keepalive probe is sent (0 = OS default).</summary>
    public int TcpKeepAliveTimeSeconds { get; set; }

    /// <summary>Seconds between OS keepalive probes while unacknowledged (0 = same as time).</summary>
    public int TcpKeepAliveIntervalSeconds { get; set; }

    /// <summary>Seconds an idle pooled connection may live before it is closed.</summary>
    public int ConnectionIdleLifetimeSeconds { get; set; } = 300;

    /// <summary>How often the pool looks for connections past their idle lifetime.</summary>
    public int ConnectionPruningIntervalSeconds { get; set; } = 30;

    /// <summary>Maximum age of a pooled connection (0 = no forced recycle).</summary>
    public int ConnectionLifetimeSeconds { get; set; }

    /// <summary>Connect timeout, so a dead link fails fast instead of stalling a request.</summary>
    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>Per-statement timeout.</summary>
    public int CommandTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Escape hatch for the guard below. Only for a deployment that has measured a
    /// reason to keep Npgsql's application-level keepalive while scale-to-zero is
    /// nominally on.
    /// </summary>
    public bool AllowKeepAliveInScaleToZeroMode { get; set; }

    /// <summary>
    /// Configuration problems that must stop startup. Called with the deployment's
    /// effective scale-to-zero switch, so the same numbers are judged in the context
    /// they will run in.
    /// </summary>
    public IReadOnlyList<string> Validate(bool scaleToZeroFriendly)
    {
        var problems = new List<string>();

        if (MinimumPoolSize < 0) problems.Add($"Database:MinimumPoolSize must be >= 0 (got {MinimumPoolSize}).");
        if (MaximumPoolSize < 1) problems.Add($"Database:MaximumPoolSize must be >= 1 (got {MaximumPoolSize}).");
        if (MinimumPoolSize > MaximumPoolSize)
            problems.Add($"Database:MinimumPoolSize ({MinimumPoolSize}) must not exceed Database:MaximumPoolSize ({MaximumPoolSize}).");

        if (KeepAliveSeconds < 0)
        {
            problems.Add($"Database:KeepAliveSeconds must be >= 0 (got {KeepAliveSeconds}).");
        }
        else if (KeepAliveSeconds > 0 && scaleToZeroFriendly && !AllowKeepAliveInScaleToZeroMode)
        {
            problems.Add(
                $"Database:KeepAliveSeconds is {KeepAliveSeconds}, but BackgroundProcessing:ScaleToZeroFriendlyMode is on. " +
                "Npgsql's application-level keepalive generates traffic on an otherwise idle connection and can keep a serverless " +
                "compute awake. Set Database:KeepAliveSeconds=0 (Npgsql's own default), or set " +
                "Database:AllowKeepAliveInScaleToZeroMode=true if you have measured why it is required, or disable " +
                "scale-to-zero-friendly mode.");
        }

        if (TcpKeepAliveIntervalSeconds > 0 && TcpKeepAliveTimeSeconds == 0)
        {
            problems.Add(
                $"Database:TcpKeepAliveIntervalSeconds ({TcpKeepAliveIntervalSeconds}) requires a non-zero " +
                "Database:TcpKeepAliveTimeSeconds.");
        }

        if ((TcpKeepAliveTimeSeconds > 0 || TcpKeepAliveIntervalSeconds > 0) && !TcpKeepAlive)
        {
            problems.Add(
                "Database:TcpKeepAliveTimeSeconds / TcpKeepAliveIntervalSeconds are set but Database:TcpKeepAlive is false, " +
                "so they would have no effect. Set Database:TcpKeepAlive=true or remove them.");
        }

        if (ConnectionIdleLifetimeSeconds < 1)
            problems.Add($"Database:ConnectionIdleLifetimeSeconds must be >= 1 (got {ConnectionIdleLifetimeSeconds}).");
        if (ConnectionPruningIntervalSeconds < 1)
            problems.Add($"Database:ConnectionPruningIntervalSeconds must be >= 1 (got {ConnectionPruningIntervalSeconds}).");
        if (ConnectionLifetimeSeconds < 0)
            problems.Add($"Database:ConnectionLifetimeSeconds must be >= 0 (got {ConnectionLifetimeSeconds}).");
        if (TimeoutSeconds < 1) problems.Add($"Database:TimeoutSeconds must be >= 1 (got {TimeoutSeconds}).");
        if (CommandTimeoutSeconds < 1) problems.Add($"Database:CommandTimeoutSeconds must be >= 1 (got {CommandTimeoutSeconds}).");

        return problems;
    }

    /// <summary>
    /// Non-fatal observations: configuration that starts but undercuts the
    /// deployment's stated intent. Logged as warnings so they are visible, not hidden.
    /// </summary>
    public IReadOnlyList<string> Warnings(bool scaleToZeroFriendly)
    {
        var warnings = new List<string>();

        if (scaleToZeroFriendly && MinimumPoolSize > 0)
        {
            warnings.Add(
                $"Database:MinimumPoolSize is {MinimumPoolSize} while scale-to-zero-friendly mode is on. Idle pooled sockets run no " +
                "queries, so this does not itself keep the compute awake, but it does keep server connections reserved. " +
                "MinimumPoolSize=0 is the documented Free-plan setting.");
        }

        if (scaleToZeroFriendly && AllowKeepAliveInScaleToZeroMode && KeepAliveSeconds > 0)
        {
            warnings.Add(
                $"Application-level keepalive is explicitly enabled ({KeepAliveSeconds}s) in a scale-to-zero deployment. " +
                "Verify on the Neon dashboard that the compute still reaches Idle.");
        }

        return warnings;
    }

    /// <summary>
    /// Non-secret summary for the startup log. Never includes a host, user, password
    /// or connection string.
    /// </summary>
    public string Describe() =>
        $"minPool={MinimumPoolSize} maxPool={MaximumPoolSize} " +
        $"appKeepalive={(KeepAliveSeconds == 0 ? "off" : $"{KeepAliveSeconds}s")} " +
        $"tcpKeepalive={(TcpKeepAlive ? $"on(time={TcpKeepAliveTimeSeconds}s,interval={TcpKeepAliveIntervalSeconds}s)" : "off")} " +
        $"idleLifetime={ConnectionIdleLifetimeSeconds}s prune={ConnectionPruningIntervalSeconds}s " +
        $"connLifetime={ConnectionLifetimeSeconds}s connectTimeout={TimeoutSeconds}s commandTimeout={CommandTimeoutSeconds}s";
}
