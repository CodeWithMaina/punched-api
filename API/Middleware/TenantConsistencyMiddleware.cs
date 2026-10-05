using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;

namespace PunchedApi.API.Middleware;

/// <summary>
/// Establishes the active <b>ITenantContext</b> and enforces the tenant
/// boundary (Phase 5 → 6).
///
/// <para><b>Invariant:</b></para>
/// <code>
/// Request Host → ITenantHostResolver → ITenantContext → Authenticated User
///   → Role/Membership Authorization → Tenant-Scoped Query → Tenant-Scoped Response
/// </code>
///
/// <para><b>Two responsibilities, one gate:</b></para>
/// <list type="bullet">
/// <item><b>1. Populate <c>TenantContext</c></b> for every request: slug (page
/// host header, then API host) → business id via <see cref="ITenantHostResolver"/>.
/// Every downstream service scopes by this value — it NARROWS results, it never
/// grants them.</item>
/// <item><b>2. Gate business-scoped operations on a tenant host</b> with
/// <see cref="IBusinessMembershipResolver"/>: the caller's global role must map
/// to an ACTIVE membership of the host business (Business → Owner,
/// Staff → Staff, Customer → Customer; Platform Admin bypasses). The host is
/// client-supplied, so it can only ever CAUSE a refusal: a spoofed header still
/// requires a real membership to pass.</item>
/// </list>
///
/// <para><b>Backward compatibility (platform root):</b> no resolvable tenant →
/// context stays inactive and the API behaves exactly as before — pure
/// identity-scoped authorization. Anonymous callers and
/// <c>[AllowAnonymous]</c> storefront reads pass untouched. Auth lifecycle
/// (<c>/v1/auth/*</c>) always passes. Identity-only surfaces
/// (<c>/v1/users/profile</c>) pass — they carry no business data, and session
/// hydration on a storefront must work for users who are not members of the
/// host.</para>
///
/// <para><b>Denials:</b> a token bound to a DIFFERENT business gets the
/// existing 409 <c>TENANT_MISMATCH</c> (frontend switch-workspace UX);
/// everything else (no membership for the host) gets 403
/// <c>TENANT_ACCESS_DENIED</c>. Never a 500, never tenant data.</para>
/// </summary>
public sealed class TenantConsistencyMiddleware
{
    /// <summary>Header the web app uses to report the tenant page it is on.</summary>
    public const string TenantHeader = "X-Punched-Tenant";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly RequestDelegate _next;
    private readonly ILogger<TenantConsistencyMiddleware> _logger;

    public TenantConsistencyMiddleware(RequestDelegate next, ILogger<TenantConsistencyMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(
        HttpContext context,
        ITenantHostResolver tenants,
        TenantContext tenant,
        IBusinessMembershipResolver memberships)
    {
        // ══ 1. Active tenant = f(page host header ?? API host) ═══════════
        var tenantSlug =
            tenants.ParseSlug(context.Request.Headers[TenantHeader].FirstOrDefault()) ??
            tenants.ParseSlug(context.Request.Host.Value);

        var hostBusinessId = tenantSlug != null
            ? await tenants.ResolveBusinessIdAsync(tenantSlug)
            : null;

        // Written for EVERY request (including anonymous) so tenant-scoped
        // services can always consult it. Inactive (null) on the platform root
        // or an unknown slug → legacy identity-scoped behaviour, unchanged.
        tenant.Set(tenantSlug, hostBusinessId);

        if (hostBusinessId == null)
        {
            await _next(context);
            return;
        }

        var path = context.Request.Path;

        // ══ 2. Auth lifecycle always reachable ══════════════════════════
        // A mismatched or non-member user must be able to refresh, log out or
        // re-authenticate — the frontend keys off this to offer "switch".
        if (path.StartsWithSegments("/v1/auth"))
        {
            await _next(context);
            return;
        }

        // ══ 3. Only the API surface is tenant-gated ══════════════════════
        // Swagger, health probes and anything outside /v1 keep working on a
        // tenant host exactly as before.
        if (!path.StartsWithSegments("/v1"))
        {
            await _next(context);
            return;
        }

        // ══ 4. Anonymous callers ═════════════════════════════════════════
        // The deny-by-default fallback policy still governs which endpoints
        // are reachable without a token; we add no second anonymous policy.
        if (context.User?.Identity?.IsAuthenticated != true)
        {
            await _next(context);
            return;
        }

        var endpoint = context.GetEndpoint();

        // ══ 5. Explicit public storefront reads ══════════════════════════
        if (endpoint?.Metadata.GetMetadata<AllowAnonymousAttribute>() != null)
        {
            await _next(context);
            return;
        }

        // ══ 6. Identity-only surfaces ════════════════════════════════════
        // The caller's own profile is not business data — storefront session
        // hydration must work for a customer who is not a member of the host.
        if (path.StartsWithSegments("/v1/users/profile"))
        {
            await _next(context);
            return;
        }

        // ══ 7. Platform Admin bypass ═════════════════════════════════════
        // The existing authorization model lets platform operators act across
        // businesses; the tenant boundary must not break that.
        if (IsInRole(context.User, "Admin"))
        {
            await _next(context);
            return;
        }

        // ══ 8. Never trust a client-supplied businessId ══════════════════
        // Route and query carriers must name the ACTIVE tenant. Catches
        // `/v1/customers/me/businesses/{businessId}` and `?businessId=` shapes
        // before the allowlist below can be used to sidestep the boundary.
        // (Body carriers are validated in the services — see EnrollAsync /
        // CreateAppointmentAsync guards.)
        if (HasForeignBusinessId(context, hostBusinessId.Value))
        {
            await Deny(context, StatusCodes.Status403Forbidden, "TENANT_ACCESS_DENIED",
                "This request targets a different business than the address you're on.");
            return;
        }

        // ══ 9. Join flows: membership-creating endpoints ═════════════════
        // A customer enrolling into the business whose host they are on, or
        // accepting a staff invitation, must work BEFORE membership exists.
        // The check above already pinned any businessId here to the host
        // tenant; services keep their own body-level guards.
        // The allowlist is shape-exact (see IsJoinFlow): the ordinary
        // customer surface — stamp cards, reviews — stays gated below.
        if (IsJoinFlow(context))
        {
            await _next(context);
            return;
        }

        // ══ 10. Role → membership gate ═══════════════════════════════════
        var userId = GetUserId(context.User);
        if (userId == null)
        {
            // Authenticated without an identity claim cannot be scoped — the
            // authorization layer would let it through, so refuse here.
            await Deny(context, StatusCodes.Status403Forbidden, "TENANT_ACCESS_DENIED",
                "Your session is missing identity information.");
            return;
        }

        var membership = await memberships.ResolveAsync(userId.Value, hostBusinessId.Value);
        if (SatisfiesRoleMembership(GetRole(context.User), membership))
        {
            await _next(context);
            return;
        }

        // ══ 11. Denial ═══════════════════════════════════════════════════
        _logger.LogInformation(
            "Tenant access refused on {Tenant} (business {Business}) for user {User}: role {Role}, membership {Kind}/{Status}",
            tenantSlug, hostBusinessId.Value, userId.Value,
            GetRole(context.User) ?? "(none)", membership.Kind, membership.Status);

        // Token bound to ANOTHER business → the existing 409 contract so the
        // frontend can offer switch-workspace; otherwise a plain 403.
        if (Guid.TryParse(context.User.FindFirst(TenantClaimNames.BusinessId)?.Value, out var tokenBusinessId) &&
            tokenBusinessId != hostBusinessId.Value)
        {
            await Deny(context, StatusCodes.Status409Conflict, "TENANT_MISMATCH",
                "You're signed in to another business. Switch workspace or sign in again to continue here.");
            return;
        }

        await Deny(context, StatusCodes.Status403Forbidden, "TENANT_ACCESS_DENIED",
            "You don't have access to this business. Your membership may have ended, or you're signed in as a different role here.");
    }

    // ── helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// True when the route or query string carries a businessId that is not
    /// the active tenant. Non-guid carriers are left to service-level guards.
    /// </summary>
    private static bool HasForeignBusinessId(HttpContext context, Guid tenantBusinessId)
    {
        // Route values are populated by UseRouting, which always runs before
        // this middleware; GetRouteData() is null-safe and returns an empty
        // RouteData when nothing matched.
        var routeValues = context.GetRouteData().Values;
        foreach (var pair in routeValues)
        {
            if (!string.Equals(pair.Key, "businessId", StringComparison.OrdinalIgnoreCase))
                continue;
            if (Guid.TryParse(pair.Value?.ToString(), out var routeId) && routeId != tenantBusinessId)
                return true;
        }

        var query = context.Request.Query["businessId"].ToString();
        if (!string.IsNullOrWhiteSpace(query) &&
            Guid.TryParse(query, out var queryId) && queryId != tenantBusinessId)
            return true;

        return false;
    }

    /// <summary>
    /// The allowlist of flows that CREATE or exist to establish membership —
    /// they must run before the membership gate, never instead of it.
    ///
    /// <para><b>Shape-exact by design.</b> A blanket namespace prefix such as
    /// <c>/v1/customers/me</c> would exempt the caller's entire stamp-card,
    /// appointment and review surface from the boundary — the very data the
    /// gate exists to protect. Only the membership lifecycle itself is
    /// exempted, and only under the verb that actually performs it.</para>
    /// </summary>
    private static bool IsJoinFlow(HttpContext context)
    {
        var path = context.Request.Path;
        var method = context.Request.Method;

        // Customer ↔ Business membership lifecycle, all on
        // /v1/customers/me/businesses (CustomerEnrollmentController):
        //   GET    (no id)              → membership probe: "[]" for a non-member
        //   POST   /{businessId}/enroll → enroll into the host business
        //   DELETE /{businessId}        → leave (scoped to the caller's own row)
        if (path.StartsWithSegments("/v1/customers/me/businesses", out var membershipTail))
        {
            var tail = (membershipTail.Value ?? string.Empty)
                .Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (tail.Length == 0 && HttpMethods.IsGet(method))
                return true;

            if (tail.Length == 1 && HttpMethods.IsDelete(method))
                return true;

            if (tail.Length == 2 && HttpMethods.IsPost(method) &&
                tail[1].Equals("enroll", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return path.StartsWithSegments("/v1/cards/enroll")   // join a loyalty program (body businessId guarded in service)
            || path.StartsWithSegments("/v1/referrals/resolve")
            || path.StartsWithSegments("/v1/invitations");   // preview + accept a staff invitation
    }

    /// <summary>
    /// Global role → required membership kind. Unknown/absent role: any
    /// active membership (never a grant — the user must still belong here).
    /// </summary>
    private static bool SatisfiesRoleMembership(string? role, BusinessMembership membership)
    {
        if (!membership.IsActive)
            return false;

        return role switch
        {
            "Business" => membership.Kind == BusinessMembershipKind.Owner,
            "Staff" => membership.Kind == BusinessMembershipKind.Staff,
            "Customer" => membership.Kind == BusinessMembershipKind.Customer,
            _ => true
        };
    }

    private static string? GetRole(ClaimsPrincipal user) =>
        user.FindFirst(ClaimTypes.Role)?.Value ?? user.FindFirst("role")?.Value;

    private static Guid? GetUserId(ClaimsPrincipal user)
    {
        var raw = user.FindFirst("userId")?.Value ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(raw, out var id) ? id : null;
    }

    private static bool IsInRole(ClaimsPrincipal user, string role) =>
        string.Equals(GetRole(user), role, StringComparison.OrdinalIgnoreCase) ||
        user.IsInRole(role);

    private static async Task Deny(HttpContext context, int statusCode, string code, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(
            ApiResponse<MessageResponse>.Fail(code, message), JsonOptions));
    }
}
