using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PunchedApi.Application.Services;

/// <summary>
/// Single source of truth for business slug rules (format, reserved names,
/// normalization, collision suffixing). Used by the DB-backed generator, the
/// seed pipeline, and the startup backfill.
///
/// NOTE: the reserved list and normalization rules are mirrored in the
/// frontend at punched-pwd/lib/tenant.ts — keep both in sync when changing.
/// The backend copy is authoritative: server-side validation always wins.
/// </summary>
public static partial class BusinessSlugPolicy
{
    /// <summary>DNS label limit — a subdomain label can never exceed 63 chars.</summary>
    public const int MaxLength = 63;

    /// <summary>Fallback base when a business name normalizes to nothing.</summary>
    public const string FallbackBase = "business";

    /// <summary>
    /// Reserved first-level labels that must never be assigned to a business:
    /// platform/infrastructure surfaces (www, api, mail…), common product
    /// areas (auth, billing, dashboard…) and environment names. A reserved
    /// base is simply skipped during generation (api → api-2) and rejected
    /// outright when an owner tries to pick it manually.
    /// </summary>
    public static readonly IReadOnlySet<string> Reserved = new HashSet<string>(StringComparer.Ordinal)
    {
        // Platform / infrastructure
        "www", "api", "app", "apps", "admin", "administrator", "root", "punched",
        "mail", "email", "smtp", "imap", "pop", "ftp", "sftp", "ssh",
        "ns", "ns1", "ns2", "dns", "mx", "webmail",
        "cdn", "static", "assets", "img", "images", "media", "files",
        "download", "downloads", "upload", "uploads",
        "ws", "wss", "websocket", "socket", "sockets",
        "hooks", "webhook", "webhooks", "integrations", "oauth", "sso",
        "vpn", "proxy", "gateway", "edge", "cloud", "internal", "intranet",
        "localhost", "local",

        // Product surfaces
        "auth", "login", "logout", "signin", "signup", "register", "verify", "reset",
        "account", "accounts", "profile", "settings", "billing", "payment", "payments",
        "pay", "checkout", "dashboard", "panel", "console", "portal",
        "support", "help", "status", "health", "security", "ssl",
        "blog", "news", "docs", "developers", "wiki", "shop", "store",

        // Environments / tooling
        "dev", "staging", "stage", "test", "tests", "testing",
        "beta", "alpha", "demo", "sandbox", "qa", "uat", "prod", "production", "live",
        "git", "github", "gitlab", "ci", "jenkins", "jira",
        "redis", "postgres", "db", "database", "elasticsearch", "grafana", "kibana",
        "metrics", "monitoring",

        // Mobile / discovery shortcuts
        "m", "mobile", "refer", "invitations", "business-register",
    };

    public static bool IsReserved(string slug) => Reserved.Contains(slug);

    /// <summary>Strict format check: lowercase letters/numbers/hyphens, no leading/trailing hyphen, 1–63 chars.</summary>
    public static bool IsValidFormat(string slug) =>
        slug.Length >= 1 && slug.Length <= MaxLength && LabelRegex().IsMatch(slug);

    /// <summary>
    /// Deterministic normalization of a business name to a slug base:
    /// strips diacritics, lowercases, collapses everything outside
    /// [a-z0-9] into single hyphens, trims hyphens, enforces the 63-char
    /// DNS limit (re-trimming any hyphen exposed by truncation), and falls
    /// back to "business" when nothing usable remains.
    /// </summary>
    public static string Slugify(string businessName)
    {
        var source = businessName ?? string.Empty;

        // Decompose so "Café" → "Cafe": combining marks drop out below.
        var decomposed = source.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                continue;
            builder.Append(ch);
        }

        var slug = InvalidCharRegex().Replace(builder.ToString().ToLowerInvariant(), "-");
        slug = TrimHyphensRegex().Replace(slug, string.Empty);

        if (slug.Length > MaxLength)
            slug = TrimHyphensRegex().Replace(slug[..MaxLength], string.Empty);

        return slug.Length == 0 ? FallbackBase : slug;
    }

    /// <summary>
    /// First free candidate for a base: base, base-2, base-3… (a reserved
    /// base counts as taken, so "Api" → "api-2"). Candidates are truncated to
    /// stay within the 63-char DNS limit; falls back to a random suffix after
    /// 100 attempts.
    /// </summary>
    public static string Next(string baseSlug, Func<string, bool> isTaken) =>
        // The async core's delegate completes synchronously (Task.FromResult),
        // so blocking here cannot deadlock.
        NextAsync(baseSlug, candidate => Task.FromResult(isTaken(candidate)))
            .GetAwaiter().GetResult();

    /// <summary>Async variant of <see cref="Next"/> for database-backed availability checks.</summary>
    public static async Task<string> NextAsync(string baseSlug, Func<string, Task<bool>> isTakenAsync)
    {
        var base63 = TruncateToLabel(baseSlug);
        for (var attempt = 1; attempt <= 100; attempt++)
        {
            var candidate = attempt == 1
                ? base63
                : TruncateToLabel($"{base63}-{attempt}");
            if (IsReserved(candidate)) continue;
            if (!await isTakenAsync(candidate)) return candidate;
        }
        return Fallback(base63);
    }

    /// <summary>
    /// Last-resort candidate: base + random suffix (numbered suffixes
    /// exhausted, or a unique-index race was lost). Practically unique.
    /// </summary>
    public static string Fallback(string baseSlug) =>
        TruncateToLabel($"{TruncateToLabel(baseSlug, MaxLength - 7)}-{Guid.NewGuid().ToString("N")[..6]}");

    /// <summary>Truncates to the 63-char DNS limit without leaving a trailing hyphen.</summary>
    public static string TruncateToLabel(string slug, int maxLength = MaxLength)
    {
        if (slug.Length <= maxLength) return slug;
        return TrimHyphensRegex().Replace(slug[..maxLength], string.Empty);
    }

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$")]
    private static partial Regex LabelRegex();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex InvalidCharRegex();

    [GeneratedRegex("^-+|-+$")]
    private static partial Regex TrimHyphensRegex();
}
