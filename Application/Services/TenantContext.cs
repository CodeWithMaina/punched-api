namespace PunchedApi.Application.Services;

/// <summary>
/// The ACTIVE TENANT for the current request — resolved server-side from the
/// page host (<c>X-Punched-Tenant</c> header, falling back to the API host) by
/// <c>TenantConsistencyMiddleware</c>. Populated BEFORE any controller or
/// service runs, so every business-scoped operation can constrain itself to it.
///
/// <para><b>Security:</b> the value derives from a client-supplied header, so
/// it is NEVER an authorization input on its own — it only <b>narrows</b> which
/// rows a caller who already passed identity + membership checks may see.
/// On the platform root there is no tenant (null) and behaviour is exactly the
/// legacy identity-scoped one. Membership itself is validated by
/// <see cref="IBusinessMembershipResolver"/> in the middleware before this
/// context is ever consulted for authorization-shaped decisions.</para>
///
/// <para>Invariant (enforced end-to-end):</para>
/// <code>
/// Request Host → ITenantHostResolver → ITenantContext → Authenticated User
///   → Role/Membership Authorization → Tenant-Scoped Query → Tenant-Scoped Response
/// </code>
/// </summary>
public interface ITenantContext
{
    /// <summary>Tenant slug parsed from the page host, or null on the platform root / unknown slug.</summary>
    string? Slug { get; }

    /// <summary>Resolved business id of the active tenant, or null when there is no tenant context.</summary>
    Guid? BusinessId { get; }

    /// <summary>True when a resolvable tenant is active for this request.</summary>
    bool IsActive { get; }
}

/// <summary>
/// Request-scoped holder written once (slug) by the tenant middleware and read
/// by services. The setter is internal to the assembly so only the middleware
/// can move the tenant boundary.
/// </summary>
public sealed class TenantContext : ITenantContext
{
    public string? Slug { get; private set; }
    public Guid? BusinessId { get; private set; }
    public bool IsActive => BusinessId.HasValue;

    /// <summary>Called by the middleware after slug → business resolution.</summary>
    public void Set(string? slug, Guid? businessId)
    {
        Slug = slug;
        BusinessId = businessId;
    }
}
