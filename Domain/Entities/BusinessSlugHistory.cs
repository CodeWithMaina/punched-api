namespace PunchedApi.Domain.Entities;

/// <summary>
/// A former slug of a business, kept forever so previously shared subdomain
/// URLs (e.g. java-house.punched.app) keep working after the owner changes
/// their address: <c>GET /v1/businesses/by-slug/{slug}</c> falls back to this
/// table, reports <c>moved=true</c> and the canonical slug, and the frontend
/// redirects the visitor to the current URL.
/// One row per superseded slug; a business may reclaim one of its own former
/// slugs, in which case the history row is removed on assignment.
/// </summary>
public class BusinessSlugHistory
{
    public Guid Id { get; set; }

    /// <summary>The superseded slug (lowercase DNS label, max 63 chars).</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>The business that used to own <see cref="Slug"/>.</summary>
    public Guid BusinessId { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>Soft-deleted businesses stop resolving; see the query filter on <c>Business</c>.</summary>
    public virtual Business? Business { get; set; }
}