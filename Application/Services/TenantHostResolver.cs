using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PunchedApi.Application.Settings;

namespace PunchedApi.Application.Services;

/// <summary>
/// Resolves the tenant context carried by a request host/header.
///
/// Two distinct responsibilities, deliberately kept apart:
/// <list type="bullet">
/// <item><see cref="ParseSlug"/> is pure and mirrors the frontend's
/// <c>parseTenantSlug</c> plus <see cref="BusinessSlugPolicy"/> — root, www,
/// reserved labels, multi-level labels and unrelated domains all yield null.</item>
/// <item><see cref="ResolveBusinessIdAsync"/> maps a slug to a BusinessId
/// (live slugs first, then superseded slug history) with a short-TTL cache,
/// the same shape as <see cref="IBusinessScopeResolver"/>.</item>
/// </list>
///
/// SECURITY: the host/slug detects a *page/tenant mismatch* and enriches auth
/// with tenant claims. It is NEVER an authorization input — every data path
/// stays scoped by the authenticated identity through
/// <c>BusinessService.CanAccessBusinessAsync</c> and the existing
/// service-level checks.
/// </summary>
public interface ITenantHostResolver
{
    /// <summary>
    /// Extracts the tenant slug from a raw host (or host-like header) value,
    /// or null when the value is the platform root / reserved / not shaped
    /// like a tenant address.
    /// </summary>
    string? ParseSlug(string? host);

    /// <summary>
    /// Validates a bare slug (format + reserved list) and resolves it to its
    /// business, including superseded slugs. Null when nothing resolves.
    /// </summary>
    Task<Guid?> ResolveBusinessIdAsync(string? slug);
}
/// <summary>
/// Parses and resolves tenant addresses. Registered as a singleton: the
/// (scoped) DbContext is resolved per lookup through IServiceScopeFactory so a
/// single cache serves every request without capturing a scoped dependency.
/// </summary>
public sealed class TenantHostResolver : ITenantHostResolver
{
    private const string CachePrefix = "tenant:slug:";
    private static readonly TimeSpan PositiveTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromSeconds(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMemoryCache _cache;
    private readonly string _rootDomain;

    public TenantHostResolver(
        IServiceScopeFactory scopeFactory,
        IMemoryCache cache,
        IOptions<PublicAppSettings> settings)
    {
        _scopeFactory = scopeFactory;
        _cache = cache;

        var configured = settings.Value.RootDomain?.Trim().Trim('.').ToLowerInvariant();
        _rootDomain = !string.IsNullOrEmpty(configured)
            ? configured
            : Uri.TryCreate(settings.Value.BaseUrl, UriKind.Absolute, out var uri)
                ? uri.Host.ToLowerInvariant()
                : "punched.app";
    }

    public string? ParseSlug(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;

        var value = host.Trim().ToLowerInvariant().TrimEnd('.');
        if (value.Length == 0 || value.StartsWith('[')) return null; // IPv6 literal

        // Drop any port ("java-house.localhost:3000").
        var colon = value.IndexOf(':');
        if (colon >= 0) value = value[..colon];
        if (value.Length == 0) return null;

        // Only addresses under the configured root carry a tenant.
        var suffix = "." + _rootDomain;
        if (!value.EndsWith(suffix, StringComparison.Ordinal)) return null;

        var label = value[..^suffix.Length];

        // A tenant address is exactly one label; deeper labels are
        // infrastructure (a.b.punched.app) and never a tenant.
        if (label.Length == 0 || label.Contains('.')) return null;

        // The same rules the backend enforces when assigning a slug, so a
        // "tenant" can never be a reserved platform surface.
        if (!BusinessSlugPolicy.IsValidFormat(label)) return null;
        if (BusinessSlugPolicy.IsReserved(label)) return null;

        return label;
    }

    public async Task<Guid?> ResolveBusinessIdAsync(string? slug)
    {
        var candidate = (slug ?? string.Empty).Trim().ToLowerInvariant();
        if (candidate.Length == 0 || !BusinessSlugPolicy.IsValidFormat(candidate)) return null;

        var resolved = await _cache.GetOrCreateAsync(CachePrefix + candidate, async entry =>
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider
                .GetRequiredService<Infrastructure.Data.ApplicationDbContext>();

            var businessId = await context.Businesses
                .AsNoTracking()
                .Where(b => b.Slug == candidate)
                .Select(b => (Guid?)b.Id)
                .FirstOrDefaultAsync();

            if (businessId == null)
            {
                // Superseded addresses still resolve so previously shared links
                // keep working (callers redirect to the canonical slug).
                businessId = await (
                    from h in context.BusinessSlugHistories.AsNoTracking()
                    join b in context.Businesses.AsNoTracking() on h.BusinessId equals b.Id
                    where h.Slug == candidate
                    select (Guid?)b.Id).FirstOrDefaultAsync();
            }

            entry.AbsoluteExpirationRelativeToNow = businessId.HasValue ? PositiveTtl : NegativeTtl;
            return businessId;
        });

        return resolved;
    }
}

