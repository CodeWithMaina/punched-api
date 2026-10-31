using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PunchedApi.Application.Authorization;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Notifications;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

/// <summary>
/// Commission configuration, ledger and lifecycle.
///
/// <para><b>When commission is earned.</b> An Earning entry is accrued when an
/// appointment reaches a terminal status that the business configuration treats
/// as qualifying (completed always; no-show and cancelled only when explicitly
/// enabled). It stays Pending while RequireSettlement is on, and is released
/// (to Payable, or to Approved when the business wants a manual release) once
/// the appointment payments settle the required ratio.</para>
///
/// <para><b>What the calculation base is.</b> The base of a line is the frozen
/// price snapshot on the appointment resource line. The platform has no discount
/// or tax concept on appointments or payments, so no component is excluded; a
/// business that gave a discount outside the platform corrects the earning with
/// an audited adjustment entry (mandatory reason) rather than by editing
/// history.</para>
///
/// <para><b>Multi-staff appointments.</b> An appointment carries one assigned
/// staff member, so every line accrues to that person. A line with no assigned
/// staff member accrues nothing - there is nobody to pay.</para>
/// </summary>
public partial class CommissionService : ICommissionService
{
    private readonly ApplicationDbContext _context;
    private readonly ICommissionResolver _resolver;
    private readonly IBusinessScopeResolver _scopeResolver;
    private readonly IPermissionService _permissions;
    private readonly INotificationService _notifications;
    private readonly ILogger<CommissionService> _logger;

    /// <summary>Active tenant (null in unit tests / root host) - scoping only, never a grant.</summary>
    private readonly ITenantContext? _tenant;

    public CommissionService(
        ApplicationDbContext context,
        ICommissionResolver resolver,
        IBusinessScopeResolver scopeResolver,
        IPermissionService permissions,
        INotificationService notifications,
        ILogger<CommissionService> logger,
        ITenantContext? tenant = null)
    {
        _context = context;
        _resolver = resolver;
        _scopeResolver = scopeResolver;
        _permissions = permissions;
        _notifications = notifications;
        _logger = logger;
        _tenant = tenant;
    }

    // ===========================================================
    //  TENANT + AUTHORIZATION HELPERS
    // ===========================================================

    /// <summary>Active tenant business id, or null on the platform root.</summary>
    private Guid? TenantBusinessId => _tenant?.IsActive == true ? _tenant.BusinessId : null;

    private static bool IsRole(string value, string expected) =>
        string.Equals(value?.Trim(), expected, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves the business the caller is acting for. Owners resolve through
    /// their Owner membership; staff through their active membership; the active
    /// tenant always narrows the answer.
    /// </summary>
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

    /// <summary>
    /// Defence-in-depth permission check. The controller already gates the
    /// endpoint, but money-moving operations are re-checked here so a future
    /// caller that forgets the attribute cannot bypass them.
    /// </summary>
    private async Task<bool> HasPermissionAsync(Guid userId, Guid businessId, string permissionCode) =>
        await _permissions.HasBusinessPermissionAsync(userId, businessId, permissionCode);

    private async Task<string> GetBusinessNameAsync(Guid businessId) =>
        await _context.Businesses.AsNoTracking()
            .Where(b => b.Id == businessId)
            .Select(b => b.Name)
            .FirstOrDefaultAsync() ?? "Your business";

    private static string Wire(string value) => value.ToLowerInvariant();
}

/// <summary>Commission configuration read and write.</summary>
public partial class CommissionService
{
    /// <inheritdoc />
    public async Task<ApiResponse<CommissionConfigResponse>> GetConfigAsync(Guid callerUserId, string role)
    {
        var businessId = await ResolveCallerBusinessIdAsync(callerUserId, role);
        if (businessId == null)
            return ApiResponse<CommissionConfigResponse>.Fail("NOT_FOUND", "No business found for this account.");

        if (!await HasPermissionAsync(callerUserId, businessId.Value, "commissions.view"))
            return ApiResponse<CommissionConfigResponse>.Fail("FORBIDDEN", "You are not allowed to view commission settings.");

        var config = await _context.CommissionConfigs.AsNoTracking()
            .FirstOrDefaultAsync(c => c.BusinessId == businessId.Value);

        return ApiResponse<CommissionConfigResponse>.Ok(MapConfig(businessId.Value, config));
    }

    /// <inheritdoc />
    public async Task<ApiResponse<CommissionConfigResponse>> UpdateConfigAsync(
        Guid callerUserId, string role, UpdateCommissionConfigRequest request)
    {
        var businessId = await ResolveCallerBusinessIdAsync(callerUserId, role);
        if (businessId == null)
            return ApiResponse<CommissionConfigResponse>.Fail("NOT_FOUND", "No business found for this account.");

        if (!await HasPermissionAsync(callerUserId, businessId.Value, "commissions.manage"))
            return ApiResponse<CommissionConfigResponse>.Fail("FORBIDDEN", "You are not allowed to change commission settings.");

        if (!TryParseCalculationType(request.CalculationType, out var calculationType))
            return ApiResponse<CommissionConfigResponse>.Fail("VALIDATION_ERROR", "calculationType must be percentage or fixed.");

        var validation = ValidateArrangement(calculationType, request.DefaultPercentage, request.DefaultFixedAmount);
        if (validation != null)
            return ApiResponse<CommissionConfigResponse>.Fail("VALIDATION_ERROR", validation);

        if (request.MinimumSettlementRatio < 0m || request.MinimumSettlementRatio > 1m)
            return ApiResponse<CommissionConfigResponse>.Fail("VALIDATION_ERROR", "minimumSettlementRatio must be between 0 and 1.");

        if (request.RoundingDecimals < 0 || request.RoundingDecimals > 4)
            return ApiResponse<CommissionConfigResponse>.Fail("VALIDATION_ERROR", "roundingDecimals must be between 0 and 4.");

        var currency = string.IsNullOrWhiteSpace(request.Currency) ? "KES" : request.Currency.Trim().ToUpperInvariant();
        if (currency.Length != 3)
            return ApiResponse<CommissionConfigResponse>.Fail("VALIDATION_ERROR", "currency must be a 3-letter ISO-4217 code.");

        var now = DateTime.UtcNow;
        var config = await _context.CommissionConfigs
            .FirstOrDefaultAsync(c => c.BusinessId == businessId.Value);

        if (config == null)
        {
            config = new CommissionConfig { Id = Guid.NewGuid(), BusinessId = businessId.Value, CreatedAt = now };
            _context.CommissionConfigs.Add(config);
        }

        config.IsEnabled = request.IsEnabled;
        config.CalculationType = calculationType;
        config.DefaultPercentage = calculationType == CommissionCalculationType.Percentage
            ? request.DefaultPercentage : null;
        config.DefaultFixedAmount = calculationType == CommissionCalculationType.Fixed
            ? request.DefaultFixedAmount : null;
        config.Currency = currency;
        config.RequireSettlement = request.RequireSettlement;
        config.RequireApproval = request.RequireApproval;
        config.EarnOnNoShow = request.EarnOnNoShow;
        config.EarnOnCancelled = request.EarnOnCancelled;
        config.AllowPartialSettlement = request.AllowPartialSettlement;
        config.MinimumSettlementRatio = request.MinimumSettlementRatio;
        config.RequireStaffServiceEligibility = request.RequireStaffServiceEligibility;
        config.RoundingDecimals = request.RoundingDecimals;
        config.Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
        config.UpdatedAt = now;

        await _context.SaveChangesAsync();

        _logger.LogInformation(
            "Commission configuration updated for business {BusinessId} by {UserId} (enabled: {Enabled}).",
            businessId.Value, callerUserId, config.IsEnabled);

        return ApiResponse<CommissionConfigResponse>.Ok(MapConfig(businessId.Value, config));
    }
}

/// <summary>Mapping and validation helpers shared by every commission operation.</summary>
public partial class CommissionService
{
    private static bool TryParseCalculationType(string? value, out CommissionCalculationType type)
    {
        type = CommissionCalculationType.Percentage;
        if (string.IsNullOrWhiteSpace(value)) return false;

        switch (value.Trim().ToLowerInvariant().Replace("_", string.Empty).Replace("-", string.Empty))
        {
            case "percentage":
            case "percent":
                type = CommissionCalculationType.Percentage;
                return true;
            case "fixed":
            case "fixedamount":
            case "flat":
                type = CommissionCalculationType.Fixed;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Validates that an arrangement is complete for its calculation type.
    /// Returns an error message, or null when the arrangement is usable.
    /// </summary>
    private static string? ValidateArrangement(
        CommissionCalculationType type, decimal? percentage, decimal? fixedAmount)
    {
        if (type == CommissionCalculationType.Percentage)
        {
            if (percentage == null) return "A percentage arrangement requires percentage.";
            if (percentage < 0m || percentage > 100m) return "percentage must be between 0 and 100.";
            return null;
        }

        if (fixedAmount == null) return "A fixed arrangement requires fixedAmount.";
        if (fixedAmount < 0m) return "fixedAmount must be zero or greater.";
        return null;
    }

    private static CommissionConfigResponse MapConfig(Guid businessId, CommissionConfig? config) => new()
    {
        BusinessId = businessId,
        IsEnabled = config?.IsEnabled ?? false,
        CalculationType = Wire((config?.CalculationType ?? CommissionCalculationType.Percentage).ToString()),
        DefaultPercentage = config?.DefaultPercentage,
        DefaultFixedAmount = config?.DefaultFixedAmount,
        Currency = config?.Currency ?? "KES",
        RequireSettlement = config?.RequireSettlement ?? true,
        RequireApproval = config?.RequireApproval ?? false,
        EarnOnNoShow = config?.EarnOnNoShow ?? false,
        EarnOnCancelled = config?.EarnOnCancelled ?? false,
        AllowPartialSettlement = config?.AllowPartialSettlement ?? true,
        MinimumSettlementRatio = config?.MinimumSettlementRatio ?? 0m,
        RequireStaffServiceEligibility = config?.RequireStaffServiceEligibility ?? true,
        RoundingDecimals = config?.RoundingDecimals ?? 2,
        Notes = config?.Notes,
        UpdatedAt = config?.UpdatedAt ?? DateTime.UtcNow
    };

    private static CommissionRateResponse MapRate(
        CommissionRate rate, string? staffName, string? serviceName) => new()
    {
        Id = rate.Id,
        Scope = Wire(rate.Scope.ToString()),
        StaffUserId = rate.StaffUserId,
        StaffName = staffName,
        ServiceId = rate.ServiceCatalogItemId,
        ServiceName = serviceName,
        CalculationType = Wire(rate.CalculationType.ToString()),
        Percentage = rate.Percentage,
        FixedAmount = rate.FixedAmount,
        IsActive = rate.IsActive,
        CreatedAt = rate.CreatedAt
    };
}

/// <summary>Scoped commission overrides (staff / service / staff+service).</summary>
public partial class CommissionService
{
    /// <inheritdoc />
    public async Task<ApiResponse<List<CommissionRateResponse>>> GetRatesAsync(Guid callerUserId, string role)
    {
        var businessId = await ResolveCallerBusinessIdAsync(callerUserId, role);
        if (businessId == null)
            return ApiResponse<List<CommissionRateResponse>>.Fail("NOT_FOUND", "No business found for this account.");

        if (!await HasPermissionAsync(callerUserId, businessId.Value, "commissions.view"))
            return ApiResponse<List<CommissionRateResponse>>.Fail("FORBIDDEN", "You are not allowed to view commission rates.");

        var rates = await _context.CommissionRates.AsNoTracking()
            .Where(r => r.BusinessId == businessId.Value)
            .OrderBy(r => r.Scope).ThenBy(r => r.CreatedAt)
            .ToListAsync();

        var staffIds = rates.Where(r => r.StaffUserId.HasValue).Select(r => r.StaffUserId!.Value).Distinct().ToList();
        var serviceIds = rates.Where(r => r.ServiceCatalogItemId.HasValue).Select(r => r.ServiceCatalogItemId!.Value).Distinct().ToList();

        var staffNames = await _context.Users.AsNoTracking()
            .Where(u => staffIds.Contains(u.Id))
            .Select(u => new { u.Id, u.FullName })
            .ToDictionaryAsync(u => u.Id, u => u.FullName);

        var serviceNames = await _context.ServiceCatalogItems.AsNoTracking()
            .Where(s => serviceIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Name })
            .ToDictionaryAsync(s => s.Id, s => s.Name);

        return ApiResponse<List<CommissionRateResponse>>.Ok(rates.Select(r => MapRate(
            r,
            r.StaffUserId.HasValue && staffNames.TryGetValue(r.StaffUserId.Value, out var sn) ? sn : null,
            r.ServiceCatalogItemId.HasValue && serviceNames.TryGetValue(r.ServiceCatalogItemId.Value, out var vn) ? vn : null
        )).ToList());
    }

    private static bool TryParseScope(string? value, out CommissionScope scope)
    {
        scope = CommissionScope.Business;
        if (string.IsNullOrWhiteSpace(value)) return false;

        switch (value.Trim().ToLowerInvariant().Replace("_", string.Empty).Replace("-", string.Empty))
        {
            case "staff":
                scope = CommissionScope.Staff;
                return true;
            case "service":
                scope = CommissionScope.Service;
                return true;
            case "staffservice":
            case "staffandservice":
                scope = CommissionScope.StaffService;
                return true;
            default:
                return false;
        }
    }
}

/// <summary>Scoped commission override writes and deletion.</summary>
public partial class CommissionService
{
    /// <inheritdoc />
    public async Task<ApiResponse<CommissionRateResponse>> UpsertRateAsync(
        Guid callerUserId, string role, UpsertCommissionRateRequest request)
    {
        var businessId = await ResolveCallerBusinessIdAsync(callerUserId, role);
        if (businessId == null)
            return ApiResponse<CommissionRateResponse>.Fail("NOT_FOUND", "No business found for this account.");

        if (!await HasPermissionAsync(callerUserId, businessId.Value, "commissions.manage"))
            return ApiResponse<CommissionRateResponse>.Fail("FORBIDDEN", "You are not allowed to change commission rates.");

        if (!TryParseScope(request.Scope, out var scope))
            return ApiResponse<CommissionRateResponse>.Fail(
                "VALIDATION_ERROR", "scope must be staff, service or staff_service.");

        // The scope must name exactly the identifiers it claims, so a rate can
        // never be stored under a key that misrepresents it.
        if ((scope is CommissionScope.Staff or CommissionScope.StaffService) && request.StaffUserId == null)
            return ApiResponse<CommissionRateResponse>.Fail("VALIDATION_ERROR", "staffUserId is required for this scope.");
        if ((scope is CommissionScope.Service or CommissionScope.StaffService) && request.ServiceId == null)
            return ApiResponse<CommissionRateResponse>.Fail("VALIDATION_ERROR", "serviceId is required for this scope.");
        if (scope == CommissionScope.Staff && request.ServiceId != null)
            return ApiResponse<CommissionRateResponse>.Fail("VALIDATION_ERROR", "serviceId is not allowed for the staff scope.");
        if (scope == CommissionScope.Service && request.StaffUserId != null)
            return ApiResponse<CommissionRateResponse>.Fail("VALIDATION_ERROR", "staffUserId is not allowed for the service scope.");

        if (!TryParseCalculationType(request.CalculationType, out var calculationType))
            return ApiResponse<CommissionRateResponse>.Fail("VALIDATION_ERROR", "calculationType must be percentage or fixed.");

        var validation = ValidateArrangement(calculationType, request.Percentage, request.FixedAmount);
        if (validation != null)
            return ApiResponse<CommissionRateResponse>.Fail("VALIDATION_ERROR", validation);

        // Both referenced entities must belong to THIS business (IDOR guard).
        string? staffName = null;
        if (request.StaffUserId is Guid staffUserId)
        {
            var isStaff = await _context.BusinessMemberships.AsNoTracking().AnyAsync(m =>
                m.BusinessId == businessId.Value &&
                m.UserId == staffUserId &&
                m.Status == BusinessMembershipStatus.Active);
            if (!isStaff)
                return ApiResponse<CommissionRateResponse>.Fail("STAFF_NOT_FOUND", "Staff member not found in this business.");

            staffName = await _context.Users.AsNoTracking()
                .Where(u => u.Id == staffUserId).Select(u => u.FullName).FirstOrDefaultAsync();
        }

        string? serviceName = null;
        if (request.ServiceId is Guid serviceId)
        {
            serviceName = await _context.ServiceCatalogItems.AsNoTracking()
                .Where(s => s.Id == serviceId && s.BusinessId == businessId.Value)
                .Select(s => s.Name)
                .FirstOrDefaultAsync();
            if (serviceName == null)
                return ApiResponse<CommissionRateResponse>.Fail("SERVICE_NOT_FOUND", "Service not found in this business.");
        }

        var scopeKey = CommissionRate.BuildScopeKey(scope, request.StaffUserId, request.ServiceId);
        var now = DateTime.UtcNow;

        var rate = await _context.CommissionRates.FirstOrDefaultAsync(r =>
            r.BusinessId == businessId.Value && r.ScopeKey == scopeKey);

        if (rate == null)
        {
            rate = new CommissionRate
            {
                Id = Guid.NewGuid(),
                BusinessId = businessId.Value,
                Scope = scope,
                StaffUserId = request.StaffUserId,
                ServiceCatalogItemId = request.ServiceId,
                ScopeKey = scopeKey,
                CreatedAt = now
            };
            _context.CommissionRates.Add(rate);
        }

        rate.CalculationType = calculationType;
        rate.Percentage = calculationType == CommissionCalculationType.Percentage ? request.Percentage : null;
        rate.FixedAmount = calculationType == CommissionCalculationType.Fixed ? request.FixedAmount : null;
        rate.IsActive = request.IsActive;
        rate.UpdatedAt = now;

        await _context.SaveChangesAsync();

        _logger.LogInformation(
            "Commission rate {ScopeKey} upserted for business {BusinessId} by {UserId}.",
            scopeKey, businessId.Value, callerUserId);

        return ApiResponse<CommissionRateResponse>.Ok(MapRate(rate, staffName, serviceName));
    }

    /// <inheritdoc />
    public async Task<ApiResponse<bool>> DeleteRateAsync(Guid callerUserId, string role, Guid rateId)
    {
        var businessId = await ResolveCallerBusinessIdAsync(callerUserId, role);
        if (businessId == null)
            return ApiResponse<bool>.Fail("NOT_FOUND", "No business found for this account.");

        if (!await HasPermissionAsync(callerUserId, businessId.Value, "commissions.manage"))
            return ApiResponse<bool>.Fail("FORBIDDEN", "You are not allowed to change commission rates.");

        var rate = await _context.CommissionRates.FirstOrDefaultAsync(r =>
            r.Id == rateId && r.BusinessId == businessId.Value);
        if (rate == null)
            return ApiResponse<bool>.Fail("NOT_FOUND", "Commission rate not found.");

        // Deleting an override never changes history: already-accrued entries
        // keep the arrangement they were calculated with.
        _context.CommissionRates.Remove(rate);
        await _context.SaveChangesAsync();

        _logger.LogInformation(
            "Commission rate {RateId} deleted for business {BusinessId} by {UserId}.",
            rateId, businessId.Value, callerUserId);

        return ApiResponse<bool>.Ok(true);
    }
}
