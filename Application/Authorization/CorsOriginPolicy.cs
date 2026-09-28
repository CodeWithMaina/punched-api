namespace PunchedApi.Application.Authorization;

/// <summary>
/// Origin matching for the frontend CORS policy.
///
/// <para><b>Why this exists.</b> <c>CorsPolicyBuilder.WithOrigins</c> compares
/// literals only — it has no notion of a wildcard. The tenant architecture needs
/// every business subdomain to be callable from the browser
/// (<c>java-house.localhost:3000</c> locally, <c>java-house.punched.app</c> in
/// production), and the list of business subdomains is unbounded. Passing
/// <c>http://*.localhost:3000</c> to <c>WithOrigins</c> therefore never matched
/// anything: no <c>Access-Control-Allow-Origin</c> was emitted, the browser
/// blocked the request, and the tenant storefront fell back to its
/// "no business lives at this address" screen.</para>
///
/// <para><b>The rule.</b> A configured entry is either an exact origin
/// (<c>https://punched.app</c>) or a single-label wildcard
/// (<c>https://*.punched.app</c>). A wildcard entry matches exactly one DNS
/// label directly beneath the configured suffix, in the same scheme, and never
/// matches the suffix itself, several labels, an empty label or a bare
/// <c>*</c> — so <c>https://evil.punched.app.attacker.test</c>,
/// <c>https://punched.app</c> and <c>https://a.b.punched.app</c> are all
/// rejected by <c>*.punched.app</c>.</para>
///
/// <para><b>Security note.</b> CORS is a browser-side control only; it never
/// protects data. Every endpoint keeps its own authorization, so widening the
/// origin list cannot grant access to anything. The matcher still stays strict
/// because it decides whether a browser is allowed to send credentials
/// (<c>AllowCredentials</c>, used for the shared cross-subdomain session
/// cookie).</para>
/// </summary>
public static class CorsOriginPolicy
{
    /// <summary>Name of the registered CORS policy used by the API.</summary>
    public const string PolicyName = "AllowFrontend";

    /// <summary>
    /// Fallback allow-list used when <c>CorsOrigins</c> is not configured:
    /// local development (root + tenant subdomains on both dev ports) and the
    /// production root, <c>www</c> and every tenant subdomain.
    /// </summary>
    public static readonly string[] DefaultAllowedOrigins =
    {
        "http://localhost:3000",      // Next.js dev (platform root)
        "http://localhost:3001",      // Alternative dev port
        "http://localhost:5091",      // Swagger/API local origin
        "http://*.localhost:3000",    // Dev with a business subdomain (java-house.localhost:3000)
        "http://*.localhost:3001",    // Alternative dev port with a business subdomain
        "https://punched.app",        // Production root
        "https://www.punched.app",    // Production www
        "https://*.punched.app",      // Production business subdomains (java-house.punched.app)
    };

    /// <summary>
    /// True when <paramref name="origin"/> matches one of
    /// <paramref name="allowedOrigins"/> (exact or single-label wildcard).
    /// Null/empty/opaque (<c>null</c>) origins never match.
    /// </summary>
    public static bool IsAllowedOrigin(string? origin, IEnumerable<string>? allowedOrigins)
    {
        if (string.IsNullOrWhiteSpace(origin) || allowedOrigins is null) return false;

        var candidate = Normalize(origin);
        // "null" is the opaque origin (sandboxed iframe / file://) — never allowed.
        if (candidate.Length == 0 || candidate == "null") return false;

        foreach (var entry in allowedOrigins)
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;

            var pattern = Normalize(entry);
            // A bare "*" is rejected on purpose: with AllowCredentials the
            // browser refuses it anyway, and silently widening the policy is
            // exactly what this class exists to prevent.
            if (pattern.Length == 0 || pattern == "*") continue;

            if (string.Equals(pattern, candidate, StringComparison.Ordinal)) return true;
            if (MatchesSingleLabelWildcard(candidate, pattern)) return true;
        }

        return false;
    }

    private static string Normalize(string value) =>
        value.Trim().TrimEnd('/').ToLowerInvariant();

    /// <summary>
    /// Matches <c>scheme://*.suffix</c> against <c>scheme://label.suffix</c>,
    /// where <c>label</c> is exactly one valid DNS label.
    /// </summary>
    private static bool MatchesSingleLabelWildcard(string origin, string pattern)
    {
        var patternSeparator = pattern.IndexOf("://", StringComparison.Ordinal);
        if (patternSeparator <= 0) return false;

        var patternHost = pattern[(patternSeparator + 3)..];
        if (patternHost.Length < 3 || !patternHost.StartsWith("*.", StringComparison.Ordinal))
            return false;

        var suffix = patternHost[1..]; // ".punched.app" / ".localhost:3000"

        var originSeparator = origin.IndexOf("://", StringComparison.Ordinal);
        if (originSeparator <= 0) return false;

        // Scheme must match exactly (http dev origins stay http).
        if (!string.Equals(
                origin[..originSeparator],
                pattern[..patternSeparator],
                StringComparison.Ordinal))
            return false;

        var originHost = origin[(originSeparator + 3)..];
        if (!originHost.EndsWith(suffix, StringComparison.Ordinal)) return false;

        var label = originHost[..^suffix.Length];
        return IsDnsLabel(label);
    }

    /// <summary>
    /// A single DNS label: 1–63 chars of <c>a–z</c>, <c>0–9</c> and <c>-</c>,
    /// not starting or ending with a hyphen (mirrors the RFC 1035 host rule and
    /// the backend's slug format).
    /// </summary>
    private static bool IsDnsLabel(string label)
    {
        if (label.Length is 0 or > 63) return false;
        if (label[0] == '-' || label[^1] == '-') return false;

        foreach (var c in label)
        {
            var allowed = c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-';
            if (!allowed) return false;
        }

        return true;
    }
}
