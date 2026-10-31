using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using PunchedApi.Application.Authorization;
using PunchedApi.Application.DTOs;

namespace PunchedApi.API.Filters;

/// <summary>
/// <b>Server-side fine-grained permission gate.</b>
///
/// <para>Decorate an action with <c>[RequirePermission("commissions.payout")]</c>
/// and the caller must hold that permission in the business they are acting for
/// — resolved from their active membership and assigned roles, never from a
/// token claim or a request field.</para>
///
/// <para>This is deliberately an <b>action filter</b> rather than an
/// <c>IAuthorizationRequirement</c>: permissions are business-scoped data, and
/// resolving them needs the same tenant context the rest of the request uses.
/// A missing permission is answered with <c>403 FORBIDDEN</c> in the platform's
/// standard error envelope, so the frontend can render it like any other
/// authorization failure.</para>
///
/// <para>Frontend route guards and hidden buttons are NOT security controls —
/// this filter is. Every protected action must carry it (or perform an
/// equivalent service-level check).</para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class RequirePermissionAttribute : TypeFilterAttribute
{
    public RequirePermissionAttribute(string permissionCode)
        : base(typeof(RequirePermissionFilter))
    {
        Arguments = new object[] { permissionCode };
    }
}

/// <summary>The filter body behind <see cref="RequirePermissionAttribute"/>.</summary>
public sealed class RequirePermissionFilter : IAsyncActionFilter
{
    private readonly IBusinessPermissionService _permissions;
    private readonly string _permissionCode;

    public RequirePermissionFilter(IBusinessPermissionService permissions, string permissionCode)
    {
        _permissions = permissions;
        _permissionCode = permissionCode;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var principal = context.HttpContext.User;

        // The endpoint's own [Authorize] policy has already run; an unauthenticated
        // caller is answered by the framework before this filter.
        if (principal?.Identity?.IsAuthenticated != true)
        {
            context.Result = new UnauthorizedResult();
            return;
        }

        var rawUserId = principal.FindFirst("userId")?.Value
                        ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (!Guid.TryParse(rawUserId, out var userId) || userId == Guid.Empty)
        {
            context.Result = Forbidden("A verified identity is required for this operation.");
            return;
        }

        var businessId = await _permissions.ResolveCallerBusinessIdAsync(userId);
        if (businessId == null)
        {
            // No business scope could be resolved: fail closed rather than
            // falling back to a persona check.
            context.Result = Forbidden("No business is associated with this account for this operation.");
            return;
        }

        if (!await _permissions.HasPermissionAsync(userId, businessId.Value, _permissionCode))
        {
            context.Result = Forbidden($"You do not have permission to perform this action ({_permissionCode} required).");
            return;
        }

        await next();
    }

    private static IActionResult Forbidden(string message) =>
        new ObjectResult(ApiResponse<object>.Fail("FORBIDDEN", message))
        {
            StatusCode = StatusCodes.Status403Forbidden
        };
}
