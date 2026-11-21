namespace PunchedApi.API.Hosting;

/// <summary>
/// Fail-fast checks for a deployed environment.
///
/// <para>Everything here is a configuration mistake that would otherwise surface
/// as a silent security or availability problem hours later: an http public URL
/// in password-reset emails, a localhost CORS origin copied from development, a
/// Redis connection with no password, an absurd body-size ceiling. The process
/// refuses to start, so the bad deployment fails at the health gate instead of
/// serving traffic.</para>
///
/// <para>Checks that already exist elsewhere are deliberately not duplicated: the
/// JWT signing secret, the database connection resolution and the R2/media
/// configuration each fail fast in <c>Program.cs</c>.</para>
/// </summary>
public static class ProductionConfiguration
{
    private const long MinimumBodyBytes = 1024 * 1024;        // 1 MB
    private const long MaximumBodyBytes = 64 * 1024 * 1024;   // 64 MB

    public static string ValidateForDeployment(
        IConfiguration configuration,
        IHostEnvironment environment,
        ILogger logger)
    {
        var identity = EnvironmentIdentity.Resolve(configuration);

        // Never let a deployed process run in ASP.NET "Development" mode: it would
        // load the developer's User Secrets and relax several checks below.
        EnvironmentIdentity.EnsureDeploymentIsNotRunningAsDevelopment(configuration, environment);

        if (!EnvironmentIdentity.IsDeployed(identity))
        {
            logger.LogInformation(
                "Environment identity is '{Identity}' (developer workstation): deployment checks are skipped; " +
                "User Secrets and appsettings.Development.json are the configuration sources.",
                identity);
            return identity;
        }

        var failures = new List<string>();

        ValidateIdentity(identity, configuration, failures);
        ValidateHttpsUrl(configuration, "PublicApp:BaseUrl", failures);
        ValidateHttpsUrl(configuration, "PublicApp:ApiBaseUrl", failures);
        ValidateCorsOrigins(configuration, failures);
        ValidateRedis(configuration, failures);
        ValidateRequestLimits(configuration, failures);
        ValidateCrossEnvironment(configuration, identity, failures);

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                $"Configuration is invalid for environment '{identity}' ({failures.Count} problem(s)):" +
                $"{Environment.NewLine} • {string.Join($"{Environment.NewLine} • ", failures)}");
        }

        WarnAboutDeliberateNonProductionSettings(configuration, identity, logger);

        // Forwarded headers are how the API learns the real client IP and scheme
        // behind Caddy. Without them every request looks like it came from the
        // proxy: per-IP rate limits collapse into one bucket and the session cookie
        // is issued without Secure. The compose stack always sets this, so a false
        // value means the process was started by hand.
        if (!configuration.GetValue("ForwardedHeaders:Enabled", false))
        {
            logger.LogWarning(
                "ForwardedHeaders:Enabled is false in environment {Identity}. If this process runs behind Caddy " +
                "(the supported topology) the client IP and request scheme will be wrong and per-IP rate limiting " +
                "will not work. Set ForwardedHeaders__Enabled=true.",
                identity);
        }

        logger.LogInformation("Configuration validated for environment {Identity}.", identity);
        return identity;
    }
    /// <summary>
    /// Checks that the declared identity is consistent with the resources it was
    /// given. Only safe metadata is inspected and reported — at most a host name,
    /// never a full connection string, a password or a key.
    /// </summary>
    private static void ValidateIdentity(string identity, IConfiguration configuration, List<string> failures)
    {
        var databaseUrl = configuration["DATABASE_URL"];
        if (string.IsNullOrWhiteSpace(databaseUrl))
        {
            failures.Add("DATABASE_URL is not set.");
            return;
        }

        if (!Uri.TryCreate(databaseUrl, UriKind.Absolute, out var uri))
        {
            failures.Add("DATABASE_URL is not a valid postgresql:// URL.");
            return;
        }

        if (!EnvironmentIdentity.IsProduction(identity))
        {
            return;
        }

        // A production process must never point at a workstation-local database.
        if (IsLocalHost(uri.Host))
        {
            failures.Add(
                $"Environment is 'production' but DATABASE_URL points at the local host '{uri.Host}'. " +
                "Production must use the managed Neon endpoint.");
        }

        if (string.Equals(configuration["MediaStorage:Provider"], "local", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add(
                "Environment is 'production' but MediaStorage:Provider is 'local' (filesystem store). Use 'r2'.");
        }
    }

    /// <summary>
    /// Explicit deny-lists that stop one environment from reaching another's
    /// resources even when a whole environment file is copied between them.
    /// </summary>
    private static void ValidateCrossEnvironment(
        IConfiguration configuration,
        string identity,
        List<string> failures)
    {
        var databaseUrl = configuration["DATABASE_URL"];
        var host = Uri.TryCreate(databaseUrl, UriKind.Absolute, out var dbUri) ? dbUri.Host : string.Empty;

        foreach (var forbidden in SplitList(configuration["APP_FORBIDDEN_DB_HOSTS"]))
        {
            if (host.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add(
                    $"Environment '{identity}' must not connect to '{host}': it matches APP_FORBIDDEN_DB_HOSTS " +
                    $"entry '{forbidden}'. Use this environment's own database.");
            }
        }

        var buckets = new[]
        {
            configuration["MediaStorage:PrivateBucket"],
            configuration["MediaStorage:PublicBucket"]
        };
        foreach (var forbidden in SplitList(configuration["APP_FORBIDDEN_STORAGE_BUCKETS"]))
        {
            foreach (var bucket in buckets)
            {
                if (!string.IsNullOrWhiteSpace(bucket)
                    && bucket.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
                {
                    failures.Add(
                        $"Environment '{identity}' must not use bucket '{bucket}': it matches " +
                        $"APP_FORBIDDEN_STORAGE_BUCKETS entry '{forbidden}'.");
                }
            }
        }
    }

    /// <summary>
    /// Settings that are legitimate but must never travel to production unnoticed.
    /// Warnings only — none of them is unsafe on its own.
    /// </summary>
    private static void WarnAboutDeliberateNonProductionSettings(
        IConfiguration configuration,
        string identity,
        ILogger logger)
    {
        if (configuration.GetValue("Daraja:Sandbox", false)
            && configuration.GetValue("Daraja:AllowMockClient", false))
        {
            logger.LogWarning(
                "Environment {Identity} uses the Daraja sandbox with the mock client: no real M-PESA request will " +
                "be made. Expected outside production; confirm before enabling live payments.",
                identity);
        }

        var explicitlyAllowedEmail = string.Equals(
            configuration["APP_ALLOW_EXTERNAL_EMAIL"], "true", StringComparison.OrdinalIgnoreCase);

        if (configuration.GetValue("Email:Enabled", false)
            && !EnvironmentIdentity.IsProduction(identity)
            && !explicitlyAllowedEmail)
        {
            logger.LogWarning(
                "Environment {Identity} has Email:Enabled=true: real messages would be sent from a non-production " +
                "identity. Set APP_ALLOW_EXTERNAL_EMAIL=true to acknowledge, or disable Email:Enabled.",
                identity);
        }

        if (!EnvironmentIdentity.IsProduction(identity) && SplitList(configuration["APP_FORBIDDEN_DB_HOSTS"]).Length == 0)
        {
            logger.LogWarning(
                "APP_FORBIDDEN_DB_HOSTS is empty in environment {Identity}. Add the production database host so a " +
                "copied environment file cannot reach live data.",
                identity);
        }
    }

    private static string[] SplitList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool IsLocalHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, "host.docker.internal", StringComparison.OrdinalIgnoreCase)
        || host.StartsWith("127.", StringComparison.Ordinal)
        || host.StartsWith("10.", StringComparison.Ordinal)
        || host.StartsWith("192.168.", StringComparison.Ordinal)
        || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".test", StringComparison.OrdinalIgnoreCase);



    private static void ValidateHttpsUrl(IConfiguration configuration, string key, List<string> failures)
    {
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value))
        {
            failures.Add($"{key} is not set. Public links (email templates, invitations, payment callbacks) need an absolute https URL.");
            return;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            failures.Add($"{key} ('{value}') is not an absolute URL.");
            return;
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            failures.Add($"{key} ('{value}') must use https in a deployed environment.");
        }
    }

    private static void ValidateCorsOrigins(IConfiguration configuration, List<string> failures)
    {
        var origins = configuration.GetSection("CorsOrigins").Get<string[]>() ?? [];
        if (origins.Length == 0)
        {
            // Same-origin deployment: the SPA calls /api on its own host, so no
            // cross-origin request exists. Informational, not a failure.
            return;
        }

        foreach (var origin in origins)
        {
            if (string.IsNullOrWhiteSpace(origin))
            {
                continue;
            }

            if (origin.Trim() == "*")
            {
                failures.Add("CorsOrigins must not contain '*': the API allows credentials, and a wildcard origin is unsafe.");
                continue;
            }

            if (origin.Contains("localhost", StringComparison.OrdinalIgnoreCase)
                || origin.Contains("127.0.0.1", StringComparison.Ordinal))
            {
                failures.Add($"CorsOrigins contains the development origin '{origin}'. List only the deployed application origins.");
                continue;
            }

            // A single-label wildcard ('https://*.example.com') is supported by
            // CorsOriginPolicy, so substitute a placeholder label before parsing.
            var probe = origin.Replace("*", "wildcard", StringComparison.Ordinal);
            if (!Uri.TryCreate(probe, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                failures.Add($"CorsOrigins entry '{origin}' must be an absolute https origin.");
            }
        }
    }

    private static void ValidateRedis(IConfiguration configuration, List<string> failures)
    {
        if (!configuration.GetValue("Redis:Enabled", false))
        {
            return;
        }

        var connectionString = configuration["Redis:ConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            failures.Add("Redis:Enabled is true but Redis:ConnectionString is empty. Set Redis__ConnectionString or disable Redis.");
            return;
        }

        if (!connectionString.Contains("password=", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add("Redis:ConnectionString has no password. The Redis service requires authentication (--requirepass).");
        }

        if (connectionString.Contains("ssl=true", StringComparison.OrdinalIgnoreCase))
        {
            // Redis lives on the Docker backend network; TLS there would only add
            // handshake cost. Flag it so a copied cloud URL is not left in place.
            failures.Add("Redis:ConnectionString requests TLS, but the internal Redis service is plain-text on the Docker backend network. Remove ssl=true.");
        }
    }

    private static void ValidateRequestLimits(IConfiguration configuration, List<string> failures)
    {
        var configured = configuration.GetValue<long?>("Http:MaxRequestBodySizeBytes");
        if (configured is not null && (configured < MinimumBodyBytes || configured > MaximumBodyBytes))
        {
            failures.Add(
                $"Http:MaxRequestBodySizeBytes ({configured}) is outside the supported range " +
                $"{MinimumBodyBytes}-{MaximumBodyBytes}. Media uploads never pass through the API " +
                "(they go straight to R2), so a large ceiling only creates an abuse vector.");
        }

        var timeout = configuration.GetValue<int?>("Http:RequestTimeoutSeconds");
        if (timeout is not null && (timeout < 5 || timeout > 300))
        {
            failures.Add($"Http:RequestTimeoutSeconds ({timeout}) must be between 5 and 300.");
        }
    }
}
