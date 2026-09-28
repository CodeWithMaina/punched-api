namespace PunchedApi.Application.Settings;

/// <summary>
/// Cross-subdomain session cookie settings (Phase 2 — shared authentication).
///
/// One sign-in must cover the platform root (<c>punched.app</c>) and every
/// business subdomain (<c>java-house.punched.app</c>). localStorage cannot do
/// that — it is origin-scoped — so the API issues a single HttpOnly refresh
/// cookie scoped to the registrable domain and each origin exchanges it for
/// its own short-lived access token (hydration), never copying tokens
/// between origins.
///
/// Configure via the "SessionCookie" section / env vars:
///   SessionCookie__Domain=.punched.app
///   SessionCookie__RequireSecure=true
///
/// <see cref="Domain"/> is intentionally empty by default: a host-only cookie
/// is what local development needs (no Domain attribute, works over http).
/// </summary>
public class SessionCookieSettings
{
    public const string SectionName = "SessionCookie";

    /// <summary>
    /// Shared cookie domain (e.g. <c>.punched.app</c>). Empty → host-only
    /// cookie, which is correct for localhost development.
    /// </summary>
    public string Domain { get; set; } = string.Empty;

    /// <summary>
    /// Force the <c>Secure</c> flag even when the incoming request is plain
    /// HTTP (used behind TLS-terminating proxies that do not forward the
    /// scheme). <c>Secure</c> is always applied on HTTPS requests regardless.
    /// </summary>
    public bool RequireSecure { get; set; }

    /// <summary>Name of the shared session (refresh) cookie.</summary>
    public string RefreshCookieName { get; set; } = "punched_session";

    /// <summary>
    /// When true the cookie is written with <c>SameSite=None</c> (requires
    /// <c>Secure</c>). Left at the safe default of Lax: the API and the web app
    /// share a registrable domain, so Lax cookies are still sent on XHR while
    /// staying immune to cross-site POST CSRF.
    /// </summary>
    public bool SameSiteNone { get; set; }
}
