using Microsoft.Extensions.Options;
using PunchedApi.Application.Settings;

namespace PunchedApi.Application.Services;

/// <summary>
/// Builds the canonical absolute tenant URLs (java-house.punched.app) used by
/// share links, referral links and the admin "Business URL" surface.
///
/// The production domain is never hardcoded: the root comes from the
/// "PublicApp" configuration (<see cref="PublicAppSettings.RootDomain"/>, with
/// the host of <see cref="PublicAppSettings.BaseUrl"/> as the fallback), so the
/// same code emits <c>http://java-house.localhost:3000</c> in development and
/// <c>https://java-house.punched.app</c> in production.
///
/// A URL is only ever produced for a slug that passes
/// <see cref="BusinessSlugPolicy.IsValidFormat"/> — never fabricated.
/// </summary>
public interface ITenantUrlBuilder
{
    /// <summary>Configured business-subdomain root (e.g. "punched.app" or "localhost").</summary>
    string RootDomain { get; }

    /// <summary>
    /// Absolute tenant URL for a slug (<c>https://{slug}.{root}{path}</c>), or
    /// null when the slug is missing/invalid — callers fall back to the root URL.
    /// </summary>
    string? BuildForSlug(string? slug, string? path = null);

    /// <summary>Absolute platform-root URL (<c>https://{root}{path}</c>).</summary>
    string BuildRoot(string? path = null);
}

/// <remarks>Singleton: pure configuration projection, no request state.</remarks>
public sealed class TenantUrlBuilder : ITenantUrlBuilder
{
    private readonly string _scheme;
    private readonly string _port;

    public TenantUrlBuilder(IOptions<PublicAppSettings> settings)
    {
        var baseUrl = settings.Value.BaseUrl;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            _scheme = "https";
            RootDomain = settings.Value.RootDomain?.Trim().Trim('.').ToLowerInvariant() ?? "punched.app";
            _port = string.Empty;
            return;
        }

        _scheme = uri.Scheme;
        var root = settings.Value.RootDomain?.Trim();
        if (string.IsNullOrWhiteSpace(root))
        {
            // Fall back to the BaseUrl host, keeping its port only when the
            // root IS that host (localhost:3000 dev) — never on a real domain.
            RootDomain = uri.Host.ToLowerInvariant();
            _port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
        }
        else
        {
            RootDomain = root.Trim('.').ToLowerInvariant();
            _port = uri.Host.Equals(RootDomain, StringComparison.OrdinalIgnoreCase) && !uri.IsDefaultPort
                ? $":{uri.Port}"
                : string.Empty;
        }
    }

    public string RootDomain { get; }

    public string? BuildForSlug(string? slug, string? path = null)
    {
        var normalized = (slug ?? string.Empty).Trim().ToLowerInvariant();
        if (!BusinessSlugPolicy.IsValidFormat(normalized)) return null;
        return $"{_scheme}://{normalized}.{RootDomain}{_port}{NormalizePath(path)}";
    }

    public string BuildRoot(string? path = null) =>
        $"{_scheme}://{RootDomain}{_port}{NormalizePath(path)}";

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        return path.StartsWith('/') ? path : "/" + path;
    }
}
