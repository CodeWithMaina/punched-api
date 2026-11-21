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

    public static void ValidateForDeployment(
        IConfiguration configuration,
        IHostEnvironment environment,
        ILogger logger)
    {
        if (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
        {
            return;
        }

        var failures = new List<string>();

        ValidateHttpsUrl(configuration, "PublicApp:BaseUrl", failures);
        ValidateHttpsUrl(configuration, "PublicApp:ApiBaseUrl", failures);
        ValidateCorsOrigins(configuration, failures);
        ValidateRedis(configuration, failures);
        ValidateRequestLimits(configuration, failures);

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                $"Production configuration is invalid for environment '{environment.EnvironmentName}' " +
                $"({failures.Count} problem(s)):{Environment.NewLine} • " +
                string.Join($"{Environment.NewLine} • ", failures));
        }

        // Forwarded headers are how the API learns the real client IP and scheme
        // behind Caddy. Without them every request looks like it came from the
        // proxy: per-IP rate limits collapse into one bucket and the session cookie
        // is issued without Secure. The compose stack always sets this, so a false
        // value means the process was started by hand.
        if (!configuration.GetValue("ForwardedHeaders:Enabled", false))
        {
            logger.LogWarning(
                "ForwardedHeaders:Enabled is false in environment {Environment}. If this process runs behind " +
                "Caddy (the supported topology) the client IP and request scheme will be wrong and per-IP rate " +
                "limiting will not work. Set ForwardedHeaders__Enabled=true.",
                environment.EnvironmentName);
        }

        logger.LogInformation(
            "Production configuration validated for environment {Environment}.", environment.EnvironmentName);
    }

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
