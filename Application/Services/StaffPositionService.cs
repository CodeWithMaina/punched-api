using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PunchedApi.Application.Authorization;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

/// <summary>
/// Business-scoped staff positions (job titles).
///
/// <para><b>A title describes what someone does; it never determines what they
/// are authorised to do.</b> Positions are deliberately decoupled from both
/// service eligibility (<c>staff_services</c>) and permissions (RBAC): assigning
/// a position grants nothing.</para>
///
/// <para><b>History.</b> Every assignment change appends a
/// <see cref="StaffPositionHistory"/> row carrying a SNAPSHOT of the label, so
/// renaming or deleting a position never rewrites what a person historically
/// held.</para>
///
/// <para><b>Tenant isolation.</b> The business is resolved server-side; a
/// position or staff id from another business resolves to "not found".</para>
/// </summary>
public partial class StaffPositionService : IStaffPositionService
{
    private const int MaxNameLength = 80;
    private const int MaxDescriptionLength = 300;
    private const int MaxPositionsPerBusiness = 100;

    private readonly ApplicationDbContext _context;
    private readonly IBusinessScopeResolver _scopeResolver;
    private readonly IPermissionService _permissions;
    private readonly ILogger<StaffPositionService> _logger;

    /// <summary>Active tenant (null on the platform root / in unit tests) - scoping only, never a grant.</summary>
    private readonly ITenantContext? _tenant;

    public StaffPositionService(
        ApplicationDbContext context,
        IBusinessScopeResolver scopeResolver,
        IPermissionService permissions,
        ILogger<StaffPositionService> logger,
        ITenantContext? tenant = null)
    {
        _context = context;
        _scopeResolver = scopeResolver;
        _permissions = permissions;
        _logger = logger;
        _tenant = tenant;
    }

    private Guid? TenantBusinessId => _tenant?.IsActive == true ? _tenant.BusinessId : null;

    private static bool IsRole(string value, string expected) =>
        string.Equals(value?.Trim(), expected, StringComparison.OrdinalIgnoreCase);

    private async Task<Guid?> ResolveCallerBusinessIdAsync(Guid callerUserId, string role)
    {
        if (TenantBusinessId is Guid tenantId) return tenantId;
        if (callerUserId == Guid.Empty) return null;

        if (IsRole(role, "Business"))
        {
            var owned = await _scopeResolver.GetOwnedBusinessIdAsync(callerUserId);
            if (owned.HasValue) return owned;
        }

        return await _scopeResolver.GetMemberBusinessIdAsync(callerUserId);
    }

    private async Task<bool> HasPermissionAsync(Guid userId, Guid businessId, string permissionCode) =>
        await _permissions.HasBusinessPermissionAsync(userId, businessId, permissionCode);

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}

/// <summary>Position read surface.</summary>
public partial class StaffPositionService
{
    /// <inheritdoc />
    public async Task<ApiResponse<List<StaffPositionResponse>>> GetPositionsAsync(
        Guid callerUserId, string role, bool includeInactive = true)
    {
        var businessId = await ResolveCallerBusinessIdAsync(callerUserId, role);
        if (businessId == null)
            return ApiResponse<List<StaffPositionResponse>>.Fail("NOT_FOUND", "No business found for this account.");

        if (!await HasPermissionAsync(callerUserId, businessId.Value, "staff.view"))
            return ApiResponse<List<StaffPositionResponse>>.Fail("FORBIDDEN", "You are not allowed to view staff positions.");

        var query = _context.StaffPositions.AsNoTracking().Where(p => p.BusinessId == businessId.Value);
        if (!includeInactive) query = query.Where(p => p.IsActive);

        var positions = await query.OrderBy(p => p.SortOrder).ThenBy(p => p.Name).ToListAsync();
        return ApiResponse<List<StaffPositionResponse>>.Ok(await MapManyAsync(positions, businessId.Value));
    }

    /// <inheritdoc />
    public async Task<ApiResponse<StaffPositionResponse>> GetPositionAsync(
        Guid callerUserId, string role, Guid positionId)
    {
        var businessId = await ResolveCallerBusinessIdAsync(callerUserId, role);
        if (businessId == null)
            return ApiResponse<StaffPositionResponse>.Fail("NOT_FOUND", "No business found for this account.");

        if (!await HasPermissionAsync(callerUserId, businessId.Value, "staff.view"))
            return ApiResponse<StaffPositionResponse>.Fail("FORBIDDEN", "You are not allowed to view staff positions.");

        var position = await _context.StaffPositions.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == positionId && p.BusinessId == businessId.Value);
        if (position == null)
            return ApiResponse<StaffPositionResponse>.Fail("NOT_FOUND", "Position not found.");

        var mapped = await MapManyAsync(new List<StaffPosition> { position }, businessId.Value);
        return ApiResponse<StaffPositionResponse>.Ok(mapped[0]);
    }
}

/// <summary>Position create surface.</summary>
public partial class StaffPositionService
{
    /// <inheritdoc />
    public async Task<ApiResponse<StaffPositionResponse>> CreatePositionAsync(
        Guid callerUserId, string role, CreateStaffPositionRequest request)
    {
        var businessId = await ResolveCallerBusinessIdAsync(callerUserId, role);
        if (businessId == null)
            return ApiResponse<StaffPositionResponse>.Fail("NOT_FOUND", "No business found for this account.");

        if (!await HasPermissionAsync(callerUserId, businessId.Value, "staff.positions.manage"))
            return ApiResponse<StaffPositionResponse>.Fail("FORBIDDEN", "You are not allowed to manage staff positions.");

        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length == 0)
            return ApiResponse<StaffPositionResponse>.Fail("VALIDATION_ERROR", "A position name is required.");
        if (name.Length > MaxNameLength)
            return ApiResponse<StaffPositionResponse>.Fail("VALIDATION_ERROR", $"A position name may be at most {MaxNameLength} characters.");

        var description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        if (description != null && description.Length > MaxDescriptionLength)
            return ApiResponse<StaffPositionResponse>.Fail("VALIDATION_ERROR", $"A description may be at most {MaxDescriptionLength} characters.");

        var existing = await _context.StaffPositions
            .Where(p => p.BusinessId == businessId.Value)
            .Select(p => new { p.Name, p.SortOrder })
            .ToListAsync();

        // Names are unique per business, case-insensitively, with a clear error
        // rather than a silent duplicate label in the assignment picker.
        if (existing.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
            return ApiResponse<StaffPositionResponse>.Fail(
                "POSITION_NAME_EXISTS", "A position with this name already exists.");

        if (existing.Count >= MaxPositionsPerBusiness)
            return ApiResponse<StaffPositionResponse>.Fail(
                "POSITION_LIMIT_REACHED", $"A business may have at most {MaxPositionsPerBusiness} positions.");

        var now = DateTime.UtcNow;
        var position = new StaffPosition
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId.Value,
            Name = name,
            Description = description,
            SortOrder = request.SortOrder ?? (existing.Count == 0 ? 0 : existing.Max(p => p.SortOrder) + 1),
            IsActive = request.IsActive ?? true,
            CreatedAt = now,
            UpdatedAt = now
        };

        _context.StaffPositions.Add(position);

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            _context.Entry(position).State = EntityState.Detached;
            return ApiResponse<StaffPositionResponse>.Fail(
                "POSITION_NAME_EXISTS", "A position with this name already exists.");
        }

        _logger.LogInformation(
            "Staff position {PositionId} created for business {BusinessId} by {UserId}.",
            position.Id, businessId.Value, callerUserId);

        return ApiResponse<StaffPositionResponse>.Ok(new StaffPositionResponse
        {
            Id = position.Id,
            BusinessId = position.BusinessId,
            Name = position.Name,
            Description = position.Description,
            SortOrder = position.SortOrder,
            IsActive = position.IsActive,
            AssignedStaffCount = 0,
            CreatedAt = position.CreatedAt,
            UpdatedAt = position.UpdatedAt
        });
    }
}

/// <summary>Position update, reorder and delete.</summary>
public partial class StaffPositionService
{
    /// <inheritdoc />
    public async Task<ApiResponse<StaffPositionResponse>> UpdatePositionAsync(
        Guid callerUserId, string role, Guid positionId, UpdateStaffPositionRequest request)
    {
        var businessId = await ResolveCallerBusinessIdAsync(callerUserId, role);
        if (businessId == null)
            return ApiResponse<StaffPositionResponse>.Fail("NOT_FOUND", "No business found for this account.");

        if (!await HasPermissionAsync(callerUserId, businessId.Value, "staff.positions.manage"))
            return ApiResponse<StaffPositionResponse>.Fail("FORBIDDEN", "You are not allowed to manage staff positions.");

        var position = await _context.StaffPositions
            .FirstOrDefaultAsync(p => p.Id == positionId && p.BusinessId == businessId.Value);
        if (position == null)
            return ApiResponse<StaffPositionResponse>.Fail("NOT_FOUND", "Position not found.");

        if (request.Name != null)
        {
            var name = request.Name.Trim();
            if (name.Length == 0)
                return ApiResponse<StaffPositionResponse>.Fail("VALIDATION_ERROR", "A position name cannot be blank.");
            if (name.Length > MaxNameLength)
                return ApiResponse<StaffPositionResponse>.Fail("VALIDATION_ERROR", $"A position name may be at most {MaxNameLength} characters.");

            if (!string.Equals(name, position.Name, StringComparison.Ordinal))
            {
                var clash = await _context.StaffPositions.AsNoTracking().AnyAsync(p =>
                    p.BusinessId == businessId.Value &&
                    p.Id != position.Id &&
                    p.Name.ToLower() == name.ToLower());
                if (clash)
                    return ApiResponse<StaffPositionResponse>.Fail(
                        "POSITION_NAME_EXISTS", "A position with this name already exists.");

                // Renaming does NOT rewrite history: existing
                // StaffPositionHistory rows keep the label they snapshotted.
                position.Name = name;
            }
        }

        if (request.Description != null)
        {
            var description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
            if (description != null && description.Length > MaxDescriptionLength)
                return ApiResponse<StaffPositionResponse>.Fail("VALIDATION_ERROR", $"A description may be at most {MaxDescriptionLength} characters.");
            position.Description = description;
        }

        // Deactivation keeps existing assignments and their history intact; the
        // label simply stops appearing in assignment pickers.
        if (request.IsActive.HasValue) position.IsActive = request.IsActive.Value;

        position.UpdatedAt = DateTime.UtcNow;

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            return ApiResponse<StaffPositionResponse>.Fail(
                "POSITION_NAME_EXISTS", "A position with this name already exists.");
        }

        _logger.LogInformation(
            "Staff position {PositionId} updated for business {BusinessId} by {UserId}.",
            position.Id, businessId.Value, callerUserId);

        var mapped = await MapManyAsync(new List<StaffPosition> { position }, businessId.Value);
        return ApiResponse<StaffPositionResponse>.Ok(mapped[0]);
    }
}

/// <summary>Position reordering and deletion.</summary>
public partial class StaffPositionService
{
    /// <inheritdoc />
    public async Task<ApiResponse<List<StaffPositionResponse>>> ReorderPositionsAsync(
        Guid callerUserId, string role, ReorderStaffPositionsRequest request)
    {
        var businessId = await ResolveCallerBusinessIdAsync(callerUserId, role);
        if (businessId == null)
            return ApiResponse<List<StaffPositionResponse>>.Fail("NOT_FOUND", "No business found for this account.");

        if (!await HasPermissionAsync(callerUserId, businessId.Value, "staff.positions.manage"))
            return ApiResponse<List<StaffPositionResponse>>.Fail("FORBIDDEN", "You are not allowed to manage staff positions.");

        var orderedIds = request.OrderedIds ?? Array.Empty<Guid>();
        if (orderedIds.Length == 0)
            return ApiResponse<List<StaffPositionResponse>>.Fail("VALIDATION_ERROR", "orderedIds must not be empty.");
        if (orderedIds.Distinct().Count() != orderedIds.Length)
            return ApiResponse<List<StaffPositionResponse>>.Fail("VALIDATION_ERROR", "orderedIds must not contain duplicates.");

        var positions = await _context.StaffPositions
            .Where(p => p.BusinessId == businessId.Value)
            .ToListAsync();
        var byId = positions.ToDictionary(p => p.Id);

        if (orderedIds.Any(id => !byId.ContainsKey(id)))
            return ApiResponse<List<StaffPositionResponse>>.Fail(
                "NOT_FOUND", "One or more positions do not belong to this business.");

        var now = DateTime.UtcNow;
        var position = 0;
        foreach (var id in orderedIds)
        {
            var row = byId[id];
            if (row.SortOrder != position)
            {
                row.SortOrder = position;
                row.UpdatedAt = now;
            }
            position++;
        }

        foreach (var row in positions.Where(p => !orderedIds.Contains(p.Id))
                     .OrderBy(p => p.SortOrder).ThenBy(p => p.Name))
        {
            if (row.SortOrder != position)
            {
                row.SortOrder = position;
                row.UpdatedAt = now;
            }
            position++;
        }

        await _context.SaveChangesAsync();

        _logger.LogInformation(
            "Staff positions reordered for business {BusinessId} by {UserId} ({Count} positions).",
            businessId.Value, callerUserId, positions.Count);

        return await GetPositionsAsync(callerUserId, role);
    }

    /// <inheritdoc />
    public async Task<ApiResponse<bool>> DeletePositionAsync(Guid callerUserId, string role, Guid positionId)
    {
        var businessId = await ResolveCallerBusinessIdAsync(callerUserId, role);
        if (businessId == null)
            return ApiResponse<bool>.Fail("NOT_FOUND", "No business found for this account.");

        if (!await HasPermissionAsync(callerUserId, businessId.Value, "staff.positions.manage"))
            return ApiResponse<bool>.Fail("FORBIDDEN", "You are not allowed to manage staff positions.");

        var position = await _context.StaffPositions
            .FirstOrDefaultAsync(p => p.Id == positionId && p.BusinessId == businessId.Value);
        if (position == null)
            return ApiResponse<bool>.Fail("NOT_FOUND", "Position not found.");

        // A position somebody currently holds is never silently deleted: the
        // caller must reassign or clear those memberships first. History rows
        // survive regardless because they snapshot the label (FK is SetNull).
        var holders = await _context.BusinessMemberships.AsNoTracking()
            .CountAsync(m => m.BusinessId == businessId.Value && m.PositionId == positionId);
        if (holders > 0)
            return ApiResponse<bool>.Fail(
                "POSITION_IN_USE",
                $"{holders} staff member(s) currently hold this position. Reassign or clear them, or deactivate the position instead.");

        _context.StaffPositions.Remove(position);
        await _context.SaveChangesAsync();

        _logger.LogInformation(
            "Staff position {PositionId} deleted for business {BusinessId} by {UserId}.",
            positionId, businessId.Value, callerUserId);

        return ApiResponse<bool>.Ok(true);
    }
}

/// <summary>Staff assignment and position history.</summary>
public partial class StaffPositionService
{
    /// <inheritdoc />
    public async Task<ApiResponse<StaffPositionAssignmentResponse>> GetStaffPositionAsync(
        Guid callerUserId, string role, Guid staffUserId)
    {
        var businessId = await ResolveCallerBusinessIdAsync(callerUserId, role);
        if (businessId == null)
            return ApiResponse<StaffPositionAssignmentResponse>.Fail("NOT_FOUND", "No business found for this account.");

        if (!await HasPermissionAsync(callerUserId, businessId.Value, "staff.view"))
            return ApiResponse<StaffPositionAssignmentResponse>.Fail("FORBIDDEN", "You are not allowed to view staff positions.");

        var membership = await _context.BusinessMemberships.AsNoTracking()
            .FirstOrDefaultAsync(m => m.BusinessId == businessId.Value && m.UserId == staffUserId);
        if (membership == null)
            return ApiResponse<StaffPositionAssignmentResponse>.Fail("STAFF_NOT_FOUND", "Staff member not found in this business.");

        var name = await _context.Users.AsNoTracking()
            .Where(u => u.Id == staffUserId).Select(u => u.FullName).FirstOrDefaultAsync();

        var position = membership.PositionId.HasValue
            ? await _context.StaffPositions.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == membership.PositionId.Value)
            : null;

        return ApiResponse<StaffPositionAssignmentResponse>.Ok(new StaffPositionAssignmentResponse
        {
            StaffUserId = staffUserId,
            StaffName = name,
            PositionId = membership.PositionId,
            PositionName = position?.Name,
            PositionIsActive = position?.IsActive,
            UpdatedAt = membership.UpdatedAt
        });
    }

    /// <inheritdoc />
    public async Task<ApiResponse<StaffPositionAssignmentResponse>> AssignStaffPositionAsync(
        Guid callerUserId, string role, Guid staffUserId, AssignStaffPositionRequest request)
    {
        var businessId = await ResolveCallerBusinessIdAsync(callerUserId, role);
        if (businessId == null)
            return ApiResponse<StaffPositionAssignmentResponse>.Fail("NOT_FOUND", "No business found for this account.");

        if (!await HasPermissionAsync(callerUserId, businessId.Value, "staff.positions.manage"))
            return ApiResponse<StaffPositionAssignmentResponse>.Fail("FORBIDDEN", "You are not allowed to manage staff positions.");

        var membership = await _context.BusinessMemberships
            .FirstOrDefaultAsync(m => m.BusinessId == businessId.Value && m.UserId == staffUserId);
        if (membership == null)
            return ApiResponse<StaffPositionAssignmentResponse>.Fail("STAFF_NOT_FOUND", "Staff member not found in this business.");

        StaffPosition? position = null;
        var clearing = request.Clear == true || request.PositionId == null;

        if (!clearing)
        {
            // The position must belong to THIS business, so a cross-tenant
            // title can never be attached to a member (IDOR guard).
            position = await _context.StaffPositions
                .FirstOrDefaultAsync(p => p.Id == request.PositionId!.Value && p.BusinessId == businessId.Value);
            if (position == null)
                return ApiResponse<StaffPositionAssignmentResponse>.Fail(
                    "POSITION_NOT_FOUND", "The position does not belong to this business.");
        }

        var note = string.IsNullOrWhiteSpace(request.Note) ? null : Truncate(request.Note.Trim(), 300);
        var now = DateTime.UtcNow;

        // Idempotent: re-assigning the same title writes no history row, so a
        // retried request cannot pollute the audit trail.
        if (membership.PositionId == (clearing ? null : position!.Id))
        {
            var unchangedName = await _context.Users.AsNoTracking()
                .Where(u => u.Id == staffUserId).Select(u => u.FullName).FirstOrDefaultAsync();

            return ApiResponse<StaffPositionAssignmentResponse>.Ok(new StaffPositionAssignmentResponse
            {
                StaffUserId = staffUserId,
                StaffName = unchangedName,
                PositionId = membership.PositionId,
                PositionName = position?.Name,
                PositionIsActive = position?.IsActive,
                UpdatedAt = membership.UpdatedAt
            });
        }

        membership.PositionId = clearing ? null : position!.Id;
        membership.UpdatedAt = now;

        // The history row snapshots the label so it stays readable even if the
        // position is later renamed, deactivated or deleted.
        _context.StaffPositionHistory.Add(new StaffPositionHistory
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId.Value,
            StaffUserId = staffUserId,
            PositionId = clearing ? null : position!.Id,
            PositionName = clearing ? null : position!.Name,
            ChangedByUserId = callerUserId,
            ChangedAt = now,
            Note = note ?? (clearing ? "Position cleared." : $"Position set to {position!.Name}."),
            CreatedAt = now
        });

        await _context.SaveChangesAsync();

        _logger.LogInformation(
            "Staff position for user {StaffUserId} in business {BusinessId} changed to {PositionId} by {UserId}.",
            staffUserId, businessId.Value, membership.PositionId, callerUserId);

        var staffName = await _context.Users.AsNoTracking()
            .Where(u => u.Id == staffUserId).Select(u => u.FullName).FirstOrDefaultAsync();

        return ApiResponse<StaffPositionAssignmentResponse>.Ok(new StaffPositionAssignmentResponse
        {
            StaffUserId = staffUserId,
            StaffName = staffName,
            PositionId = membership.PositionId,
            PositionName = clearing ? null : position!.Name,
            PositionIsActive = clearing ? null : position!.IsActive,
            UpdatedAt = membership.UpdatedAt
        });
    }
}

/// <summary>Position history and mapping helpers.</summary>
public partial class StaffPositionService
{
    /// <inheritdoc />
    public async Task<ApiResponse<List<StaffPositionHistoryResponse>>> GetPositionHistoryAsync(
        Guid callerUserId, string role, Guid? staffUserId)
    {
        var businessId = await ResolveCallerBusinessIdAsync(callerUserId, role);
        if (businessId == null)
            return ApiResponse<List<StaffPositionHistoryResponse>>.Fail("NOT_FOUND", "No business found for this account.");

        if (!await HasPermissionAsync(callerUserId, businessId.Value, "staff.view"))
            return ApiResponse<List<StaffPositionHistoryResponse>>.Fail("FORBIDDEN", "You are not allowed to view staff positions.");

        // A staff caller may only read their own history.
        if (IsRole(role, "Staff") && staffUserId.HasValue && staffUserId.Value != callerUserId)
            return ApiResponse<List<StaffPositionHistoryResponse>>.Fail("FORBIDDEN", "You can only view your own position history.");

        var effectiveStaffId = IsRole(role, "Staff") ? callerUserId : staffUserId;

        var query = _context.StaffPositionHistory.AsNoTracking()
            .Where(h => h.BusinessId == businessId.Value);
        if (effectiveStaffId.HasValue) query = query.Where(h => h.StaffUserId == effectiveStaffId.Value);

        var rows = await query.OrderByDescending(h => h.ChangedAt).Take(500).ToListAsync();
        if (rows.Count == 0)
            return ApiResponse<List<StaffPositionHistoryResponse>>.Ok(new List<StaffPositionHistoryResponse>());

        var staffIds = rows.Select(r => r.StaffUserId).Distinct().ToList();
        var names = await _context.Users.AsNoTracking()
            .Where(u => staffIds.Contains(u.Id))
            .Select(u => new { u.Id, u.FullName })
            .ToDictionaryAsync(u => u.Id, u => u.FullName);

        return ApiResponse<List<StaffPositionHistoryResponse>>.Ok(rows.Select(r => new StaffPositionHistoryResponse
        {
            Id = r.Id,
            StaffUserId = r.StaffUserId,
            StaffName = names.TryGetValue(r.StaffUserId, out var name) ? name : null,
            PositionId = r.PositionId,
            PositionName = r.PositionName,
            ChangedByUserId = r.ChangedByUserId,
            ChangedAt = r.ChangedAt,
            Note = r.Note
        }).ToList());
    }

    private async Task<List<StaffPositionResponse>> MapManyAsync(
        List<StaffPosition> positions, Guid businessId)
    {
        if (positions.Count == 0) return new List<StaffPositionResponse>();

        var ids = positions.Select(p => p.Id).ToList();

        // One grouped count query instead of one query per position (no N+1).
        var counts = (await _context.BusinessMemberships.AsNoTracking()
                .Where(m => m.BusinessId == businessId && m.PositionId.HasValue && ids.Contains(m.PositionId!.Value))
                .GroupBy(m => m.PositionId!.Value)
                .Select(g => new { PositionId = g.Key, Count = g.Count() })
                .ToListAsync())
            .ToDictionary(x => x.PositionId, x => x.Count);

        return positions.Select(p => new StaffPositionResponse
        {
            Id = p.Id,
            BusinessId = p.BusinessId,
            Name = p.Name,
            Description = p.Description,
            SortOrder = p.SortOrder,
            IsActive = p.IsActive,
            AssignedStaffCount = counts.TryGetValue(p.Id, out var value) ? value : 0,
            CreatedAt = p.CreatedAt,
            UpdatedAt = p.UpdatedAt
        }).ToList();
    }
}
