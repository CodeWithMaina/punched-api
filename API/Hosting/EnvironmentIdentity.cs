namespace PunchedApi.API.Hosting;

/// <summary>
/// The environment this process believes it is running in.
///
/// <para>Deliberately <b>explicit</b> rather than inferred from a hostname or a
/// naming convention: <c>APP_ENV_NAME</c> is written into the environment file
/// (<c>/etc/punched/production.env</c>, <c>development.env</c>, …) and travels
/// into the container with the rest of the configuration. A hostname is not
/// proof of identity — a DNS mistake must not turn production into "staging".</para>
///
/// <para>Resolution order:</para>
/// <list type="number">
///   <item><c>APP_ENV_NAME</c> environment variable (deployed profiles).</item>
///   <item><c>Punched:EnvironmentName</c> from appsettings (local default).</item>
///   <item>Derived from <c>ASPNETCORE_ENVIRONMENT</c>: Development/Testing →
///   <c>local</c>; Staging → <c>staging</c>; Production → <c>production</c>.</item>
/// </list>
///
/// <para>An unknown value throws: a typo must fail startup, never skip the
/// environment checks by falling through to "unknown".</para>
/// </summary>
public static class EnvironmentIdentity
{
    public const string EnvironmentVariable = "APP_ENV_NAME";
    public const string ConfigurationKey = "Punched:EnvironmentName";

    public const string Local = "local";
    public const string Development = "development";
    public const string Staging = "staging";
    public const string Production = "production";

    private static readonly string[] Known = [Local, Development, Staging, Production];

    public static string Resolve(IConfiguration configuration)
    {
        var declared = configuration[EnvironmentVariable] ?? configuration[ConfigurationKey];
        if (!string.IsNullOrWhiteSpace(declared))
        {
            var normalized = declared.Trim().ToLowerInvariant();
            if (Known.Contains(normalized))
            {
                return normalized;
            }

            throw new InvalidOperationException(
                $"{EnvironmentVariable}='{declared}' is not a recognised environment. " +
                $"Use one of: {string.Join(", ", Known)}.");
        }

        // No explicit identity: fall back to the ASP.NET Core environment name.
        var aspnet = configuration["ASPNETCORE_ENVIRONMENT"]?.Trim().ToLowerInvariant();
        return aspnet switch
        {
            null or "" or "development" or "testing" => Local,
            "staging" => Staging,
            "production" => Production,
            _ => throw new InvalidOperationException(
                $"ASPNETCORE_ENVIRONMENT='{aspnet}' cannot be mapped to an environment identity. " +
                $"Set {EnvironmentVariable} explicitly (one of: {string.Join(", ", Known)}).")
        };
    }

    /// <summary>Anything that is not a developer workstation: runs from a deployment.</summary>
    public static bool IsDeployed(string identity) =>
        !string.Equals(identity, Local, StringComparison.Ordinal);

    public static bool IsProduction(string identity) =>
        string.Equals(identity, Production, StringComparison.Ordinal);

    /// <summary>
    /// Guards the one configuration mistake that would silently weaken everything:
    /// a container started with <c>ASPNETCORE_ENVIRONMENT=Development</c>. In that
    /// mode ASP.NET Core loads the developer's User Secrets, and this application
    /// relaxes several deployment checks (JWT placeholder, local object store,
    /// boot-time migrations). On a deployed host that is never intentional, so it
    /// has to be asked for explicitly.
    /// </summary>
    public static void EnsureDeploymentIsNotRunningAsDevelopment(
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        if (!environment.IsDevelopment())
        {
            return;
        }

        var inContainer = string.Equals(
            configuration["DOTNET_RUNNING_IN_CONTAINER"], "true", StringComparison.OrdinalIgnoreCase);
        var markedDeployed = string.Equals(
            configuration["PUNCHED_DEPLOYED"], "true", StringComparison.OrdinalIgnoreCase);
        var explicitlyAllowed = string.Equals(
            configuration["Punched:AllowDeployedDevelopment"], "true", StringComparison.OrdinalIgnoreCase);

        if ((inContainer || markedDeployed) && !explicitlyAllowed)
        {
            throw new InvalidOperationException(
                "ASPNETCORE_ENVIRONMENT=Development is not allowed in a deployed process "
                + $"(DOTNET_RUNNING_IN_CONTAINER={inContainer}, PUNCHED_DEPLOYED={markedDeployed}). "
                + "Development mode loads the developer's User Secrets and relaxes deployment checks. "
                + "Set ASPNETCORE_ENVIRONMENT=Production and select the profile with APP_ENV_NAME "
                + "(local|development|staging|production). If you really need a Development container, "
                + "set Punched__AllowDeployedDevelopment=true.");
        }
    }
}
