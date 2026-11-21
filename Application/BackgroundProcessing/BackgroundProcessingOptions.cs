namespace PunchedApi.Application.BackgroundProcessing;

/// <summary>
/// Resolves a <see cref="PollingProfile"/> for a named worker from configuration.
///
/// <para>Resolution order, lowest precedence first:</para>
/// <list type="number">
///   <item>the code defaults on <see cref="PollingProfile"/>;</item>
///   <item><c>BackgroundProcessing:*</c> — shared settings for every worker;</item>
///   <item><c>BackgroundProcessing:Workers:&lt;name&gt;:*</c> — that worker only.</item>
/// </list>
///
/// <para>Only keys that are present are applied, so an override section contains just
/// the values that differ. Nothing is cached: workers resolve their profile once at
/// startup, which keeps a configuration reload from changing live loops mid-flight.</para>
/// </summary>
public sealed class BackgroundProcessingOptions
{
    public const string SectionName = "BackgroundProcessing";
    public const string WorkersSectionName = "Workers";

    /// <summary>Stable worker names used as configuration keys and metric tags.</summary>
    public static class Names
    {
        public const string Notifications = "notifications";
        public const string DomainEvents = "domain-events";
        public const string ScheduledJobs = "scheduled-jobs";
        public const string PaymentLedger = "payment-ledger";
        public const string PaymentExpiry = "payment-expiry";
        public const string RescheduleExpiry = "reschedule-expiry";
        public const string SubscriptionExpiry = "subscription-expiry";
        public const string Payouts = "payouts";
        public const string Analytics = "analytics";
        public const string Cleanup = "cleanup";
        public const string MediaProcessing = "media-processing";
        public const string MediaCleanup = "media-cleanup";
    }

    /// <summary>Every worker this application knows about, in startup-summary order.</summary>
    public static readonly string[] AllWorkers =
    {
        Names.Notifications,
        Names.DomainEvents,
        Names.ScheduledJobs,
        Names.PaymentLedger,
        Names.PaymentExpiry,
        Names.RescheduleExpiry,
        Names.SubscriptionExpiry,
        Names.Payouts,
        Names.Analytics,
        Names.Cleanup,
        Names.MediaProcessing,
        Names.MediaCleanup
    };

    private readonly IConfiguration _configuration;

    public BackgroundProcessingOptions(IConfiguration configuration)
    {
        _configuration = configuration;
        // Bind once at startup so the summary reflects the same values the workers use.
        Profiles = AllWorkers.ToDictionary(name => name, For, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The resolved profile per known worker.</summary>
    public IReadOnlyDictionary<string, PollingProfile> Profiles { get; }

    /// <summary>Resolves (or re-resolves) the profile for one worker.</summary>
    public PollingProfile For(string workerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerName);

        var profile = new PollingProfile();

        if (string.Equals(workerName, "default", StringComparison.OrdinalIgnoreCase))
        {
            _configuration.GetSection(SectionName).Bind(profile);
            profile.ScaleToZeroFriendly = _configuration.GetValue(
                $"{SectionName}:ScaleToZeroFriendlyMode", profile.ScaleToZeroFriendly);
            return profile;
        }

        _configuration.GetSection(SectionName).Bind(profile);
        _configuration.GetSection($"{SectionName}:{WorkersSectionName}:{workerName}").Bind(profile);

        // The mode switch is deliberately a root-level key: flipping the database
        // profile in one place must move every worker, including ones whose section
        // an operator has customised.
        profile.ScaleToZeroFriendly = _configuration.GetValue(
            $"{SectionName}:ScaleToZeroFriendlyMode", profile.ScaleToZeroFriendly);

        return profile;
    }

    /// <summary>True when the shared scale-to-zero mode is on (used by the summary).</summary>
    public bool ScaleToZeroFriendly =>
        _configuration.GetValue($"{SectionName}:ScaleToZeroFriendlyMode", false);

    /// <summary>
    /// Validates every resolved profile. Returns the list of problems (empty = valid)
    /// so startup can fail with all of them at once rather than one per restart.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        foreach (var (name, profile) in Profiles)
        {
            var problem = profile.Validate(name);
            if (problem is not null) problems.Add(problem);
        }

        return problems;
    }

    /// <summary>
    /// The startup summary: one line per worker, no connection strings, no secrets.
    /// This is the single place an operator can confirm which polling behaviour a
    /// deployment is really running.
    /// </summary>
    public IReadOnlyList<string> DescribeLines()
    {
        var lines = new List<string>(Profiles.Count + 2)
        {
            $"Scale-to-zero-friendly mode: {(ScaleToZeroFriendly ? "ON (idle polling is lifted and aligned so Neon can suspend)" : "OFF (latency-first polling)")}"
        };

        foreach (var (name, profile) in Profiles)
        {
            lines.Add(profile.Enabled
                ? $"{name,-20} {profile.Describe()}"
                : $"{name,-20} disabled");
        }

        return lines;
    }
}
