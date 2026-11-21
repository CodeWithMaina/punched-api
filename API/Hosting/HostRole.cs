namespace PunchedApi.API.Hosting;

/// <summary>
/// The process role selected by <c>PUNCHED_ROLE</c>.
///
/// <para>One image runs as two containers on the VPS:</para>
/// <list type="bullet">
///   <item><c>api</c> — serves HTTP. <c>WORKERS_ENABLED=false</c>, so no
///   <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> competes with
///   request threads, and the API can be scaled or restarted without touching
///   queued work.</item>
///   <item><c>worker</c> — runs every hosted service and nothing else: no
///   controller routes are mapped, only the health endpoints are exposed (on the
///   internal Docker network, never published), so a worker can be probed and
///   rolled out independently.</item>
/// </list>
///
/// <para>The default is <c>api</c> so an environment that never sets the variable
/// (local development, tests, an existing single-container deployment) keeps
/// exactly the behaviour it has today.</para>
/// </summary>
public static class HostRole
{
    public const string ConfigurationKey = "PUNCHED_ROLE";
    public const string Api = "api";
    public const string Worker = "worker";

    /// <summary>Resolved role name, validated. Throws on an unrecognised value.</summary>
    public static string Resolve(IConfiguration configuration)
    {
        var raw = configuration[ConfigurationKey];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Api;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            Api => Api,
            Worker => Worker,
            _ => throw new InvalidOperationException(
                $"{ConfigurationKey} must be '{Api}' or '{Worker}' (received '{raw}'). " +
                "An unrecognised role would silently start HTTP endpoints in a worker container or " +
                "background loops in an API container, so startup is refused instead.")
        };
    }

    public static bool IsWorker(IConfiguration configuration) =>
        string.Equals(Resolve(configuration), Worker, StringComparison.Ordinal);
}
