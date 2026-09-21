using PunchedApi.Application.Authorization;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;

namespace PunchedApi.Application.Loyalty;

/// <summary>The server-resolved identity of a Loyalty caller.</summary>
public sealed record LoyaltyActor(Guid UserId, string Role, Guid BusinessId);

/// <summary>
/// Outcome of resolving a Loyalty caller. <see cref="Actor"/> is null on failure.
/// </summary>
public sealed record LoyaltyScopeResult(LoyaltyActor? Actor, string? ErrorCode, string? ErrorMessage)
{
    public bool Success => Actor != null;

    public static LoyaltyScopeResult Ok(LoyaltyActor actor) => new(actor, null, null);

    public static LoyaltyScopeResult Fail(string code, string message) => new(null, code, message);
}

/// <summary>
/// Resolves the calling business/staff user and their tenant — always
/// server-side, never from a request body, route or query value.
/// Shared by every Loyalty service so tenant scoping cannot drift per-endpoint.
/// </summary>
public interface ILoyaltyScopeResolver
{
    /// <summary>
    /// Resolves the actor and asserts they hold <paramref name="permissionCode"/>.
    /// Staff must be linked to a business.
    /// </summary>
    Task<LoyaltyScopeResult> ResolveAsync(Guid userId, string permissionCode);

    /// <summary>
    /// Resolves the actor without a permission assertion (used by read paths
    /// that are already role-gated).
    /// </summary>
    Task<LoyaltyScopeResult> ResolveAsync(Guid userId);
}

/// <inheritdoc />
public sealed class LoyaltyScopeResolver : ILoyaltyScopeResolver
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IBusinessScopeResolver _businessScopeResolver;
    private readonly IPermissionService _permissionService;

    public LoyaltyScopeResolver(
        IUnitOfWork unitOfWork,
        IBusinessScopeResolver businessScopeResolver,
        IPermissionService permissionService)
    {
        _unitOfWork = unitOfWork;
        _businessScopeResolver = businessScopeResolver;
        _permissionService = permissionService;
    }

    /// <inheritdoc />
    public async Task<LoyaltyScopeResult> ResolveAsync(Guid userId) =>
        await ResolveCoreAsync(userId, permissionCode: null);

    /// <inheritdoc />
    public async Task<LoyaltyScopeResult> ResolveAsync(Guid userId, string permissionCode) =>
        await ResolveCoreAsync(userId, permissionCode);

    private async Task<LoyaltyScopeResult> ResolveCoreAsync(Guid userId, string? permissionCode)
    {
        var actor = await _unitOfWork.Users.GetByIdAsync(userId);
        if (actor == null)
            return LoyaltyScopeResult.Fail("UNAUTHORIZED", "Authenticated user not found.");

        var roleName = actor.Role.ToString();

        Guid? businessId = actor.Role switch
        {
            // Owner: resolved from the business they own, via the cached resolver.
            UserRole.Business => await _businessScopeResolver.GetOwnedBusinessIdAsync(actor.Id),

            // Staff: the business they are linked to.
            UserRole.Staff => actor.StaffBusinessId,

            _ => null
        };

        if (businessId == null)
            return actor.Role == UserRole.Business
                ? LoyaltyScopeResult.Fail("NOT_FOUND", "No business found for this account.")
                : LoyaltyScopeResult.Fail("NOT_LINKED", "Staff user is not linked to a business.");

        if (permissionCode != null && !_permissionService.HasPermission(roleName, permissionCode))
            return LoyaltyScopeResult.Fail(
                "FORBIDDEN",
                $"You do not have permission to perform this action ({permissionCode} required).");

        return LoyaltyScopeResult.Ok(new LoyaltyActor(actor.Id, roleName, businessId.Value));
    }
}