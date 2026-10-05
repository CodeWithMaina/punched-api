using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using PunchedApi.Application.Settings;

namespace PunchedApi.Application.Services;

/// <summary>
/// Issues and clears the shared cross-subdomain session cookie (Phase 2).
///
/// The cookie carries ONLY the refresh token, is HttpOnly (never readable by
/// JavaScript, so it cannot leak through XSS), Secure on HTTPS, SameSite=Lax
/// (the web app and the API share a registrable domain, so the cookie is still
/// sent on XHR while cross-site POSTs stay blocked), and — when configured —
/// scoped to the registrable domain so one sign-in covers
/// <c>punched.app</c>, <c>java-house.punched.app</c> and every other tenant.
///
/// Access tokens are never placed in this cookie: each origin exchanges the
/// shared session for its own short-lived access token (hydration).
/// </summary>
public interface ISessionCookieService
{
    /// <summary>Reads the shared refresh token from the request, if present.</summary>
    string? Read(HttpRequest request);

    /// <summary>
    /// Writes (or refreshes) the shared session cookie. Expiry defaults to the
    /// access-token rotation window (JwtSettings.RefreshTokenExpiryDays) so the
    /// cookie and the server-side refresh session stay in step.
    /// </summary>
    void Write(HttpRequest request, HttpResponse response, string refreshToken, DateTime? expiresAtUtc = null);

    /// <summary>Expires the shared session cookie (logout).</summary>
    void Clear(HttpRequest request, HttpResponse response);
}

/// <remarks>Singleton: cookie options are configuration + scheme derived.</remarks>
public sealed class SessionCookieService : ISessionCookieService
{
    private readonly SessionCookieSettings _settings;
    private readonly JwtSettings _jwtSettings;

    public SessionCookieService(IOptions<SessionCookieSettings> settings, IOptions<JwtSettings> jwtSettings)
    {
        _settings = settings.Value;
        _jwtSettings = jwtSettings.Value;
    }

    public string CookieName => _settings.RefreshCookieName;

    public string? Read(HttpRequest request) =>
        request.Cookies.TryGetValue(_settings.RefreshCookieName, out var value) &&
        !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    public void Write(HttpRequest request, HttpResponse response, string refreshToken, DateTime? expiresAtUtc = null)
    {
        if (string.IsNullOrWhiteSpace(refreshToken)) return;

        // Never shorten an existing session: the cookie lives exactly as long as
        // the refresh session it carries.
        var expires = expiresAtUtc ?? DateTime.UtcNow.AddDays(_jwtSettings.RefreshTokenExpiryDays);
        response.Cookies.Append(_settings.RefreshCookieName, refreshToken, BuildOptions(request, expires));
    }

    public void Clear(HttpRequest request, HttpResponse response)
    {
        // Same attributes as when written — a cookie is only removed when the
        // name, path and domain all match the original.
        var options = BuildOptions(request, DateTime.UnixEpoch);
        response.Cookies.Delete(_settings.RefreshCookieName, options);
    }

    private CookieOptions BuildOptions(HttpRequest request, DateTime? expiresAtUtc)
    {
        var options = new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            Path = "/",
            Secure = _settings.RequireSecure || request.IsHttps,
            SameSite = _settings.SameSiteNone ? SameSiteMode.None : SameSiteMode.Lax
        };

        if (expiresAtUtc.HasValue)
            options.Expires = expiresAtUtc.Value;

        // Empty Domain → host-only cookie (localhost development).
        if (!string.IsNullOrWhiteSpace(_settings.Domain))
            options.Domain = _settings.Domain.Trim();

        return options;
    }
}
