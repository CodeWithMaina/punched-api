namespace PunchedApi.API.Hosting;

/// <summary>
/// Health-probe tuning (<c>Health</c> section / <c>Health__*</c> variables).
///
/// <para><b>Why it is configurable.</b> Readiness answers "can this process serve a
/// database-dependent request right now?", and on a serverless database every probe
/// is also activity that keeps the compute awake. Docker's health check therefore
/// probes <c>/health/live</c> (no dependencies) and readiness is reserved for
/// deployment gates and operators. These values bound the cost of the probes that do
/// reach the database.</para>
/// </summary>
public sealed class HealthOptions
{
    public const string SectionName = "Health";

    /// <summary>
    /// How long a successful database probe is trusted. Docker probes every 20 s;
    /// without this, every probe of every container costs a Neon round trip.
    /// </summary>
    public int DatabaseProbeCacheSeconds { get; set; } = 15;

    /// <summary>Per-probe timeout, so a waking compute cannot hold a probe open.</summary>
    public int DatabaseProbeTimeoutSeconds { get; set; } = 5;

    /// <summary>How long a failed database probe is remembered (kept short: recovery must show quickly).</summary>
    public int DatabaseProbeNegativeCacheSeconds { get; set; } = 2;

    /// <summary>How long a successful/failed Redis ping is reused.</summary>
    public int RedisProbeCacheSeconds { get; set; } = 5;

    /// <summary>Redis ping timeout. Redis is a cache; a few hundred ms is already generous.</summary>
    public int RedisProbeTimeoutSeconds { get; set; } = 1;
}
