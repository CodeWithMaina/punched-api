using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PunchedApi.Application.Authorization;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

/// <summary>
/// Owner-side staff management: profile, services, working week, time off and
/// active/suspended state.
///
/// <para><b>Scope.</b> The business is always resolved from the caller (active
/// tenant, else owned business, else active membership) — never from a request
/// body — so another business's staff id resolves to "not found".</para>
///
/// <para><b>No destructive writes.</b> There is no staff delete: deactivation
/// suspends the membership, which removes the person from new bookings, new
/// availability and new attendance while leaving every historical appointment,
/// attendance event and commission entry intact and readable.</para>
///
/// <para><b>Working hours.</b> The recurring week lives in
/// <see cref="StaffAvailability"/> (one row per weekday) and dated rows in
/// <see cref="StaffShift"/> override it for a single date — which is how a day off
/// is expressed. The booking engine resolves in exactly that order, so what the
/// owner saves here is what customers can book.</para>
/// </summary>
public partial class StaffManagementService : IStaffManagementService
{
    /// <summary>How far ahead dated time off is listed for a staff member.</summary>
    private const int TimeOffHorizonDays = 180;

    private static readonly string[] DayNames =
        ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

    private readonly ApplicationDbContext _context;
    private readonly IBusinessScopeResolver _scopeResolver;
    private readonly IPermissionService _permissions;
    private readonly ILogger<StaffManagementService> _logger;

    /// <summary>Active tenant (null on the platform root / in unit tests) — scoping only, never a grant.</summary>
    private readonly ITenantContext? _tenant;

    public StaffManagementService(
        ApplicationDbContext context,
        IBusinessScopeResolver scopeResolver,
        IPermissionService permissions,
        ILogger<StaffManagementService> logger,
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

    private Task<bool> HasPermissionAsync(Guid userId, Guid businessId, string permissionCode) =>
        _permissions.HasBusinessPermissionAsync(userId, businessId, permissionCode);

    /// <summary>
    /// Resolves the caller's business and the staff membership inside it in one
    /// step, so every action shares the same isolation rule: a membership of
    /// another business is indistinguishable from one that does not exist.
    /// </summary>
    private async Task<(Guid BusinessId, BusinessMembership Membership, User User)?> ResolveStaffAsync(
        Guid callerUserId, string role, Guid staffUserId)
    {
        var businessId = await ResolveCallerBusinessIdAsync(callerUserId, role);
        if (businessId == null) return null;

        var membership = await _context.BusinessMemberships
            .FirstOrDefaultAsync(m => m.BusinessId == businessId.Value && m.UserId == staffUserId);

        if (membership == null || membership.Role == BusinessMembershipRole.Customer)
            return null;

        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == staffUserId && !u.IsDeleted);
        if (user == null) return null;

        return (businessId.Value, membership, user);
    }

    private static int? NormalizeGoal(int? value) =>
        value is null ? null : Math.Clamp(value.Value, 1, 1000);

    // ===========================================================
    //  READ — one member's management profile
    // ===========================================================

    /// <inheritdoc />
    public async Task<ApiResponse<StaffDetailResponse>> GetStaffDetailAsync(
        Guid callerUserId, string role, Guid staffUserId)
    {
        var resolved = await ResolveStaffAsync(callerUserId, role, staffUserId);
        if (resolved == null)
            return ApiResponse<StaffDetailResponse>.Fail("NOT_FOUND", "Staff member not found in your business.");

        var (businessId, membership, user) = resolved.Value;

        if (!await HasPermissionAsync(callerUserId, businessId, "staff.view"))
            return ApiResponse<StaffDetailResponse>.Fail("FORBIDDEN", "You are not allowed to view staff.");

        var business = await _context.Businesses.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == businessId);
        if (business == null)
            return ApiResponse<StaffDetailResponse>.Fail("NOT_FOUND", "No business found for this account.");

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var weekStart = today.AddDays(-6);
        var todayUtc = DateTime.UtcNow.Date;
        var weekStartUtc = todayUtc.AddDays(-6);

        // Counters are composed into the SAME statement as the identity read:
        // one round trip, no N+1, and no second source of truth for numbers the
        // staff list already reports.
        var detail = await _context.Users
            .AsNoTracking()
            .Where(u => u.Id == staffUserId && !u.IsDeleted)
            .Select(u => new StaffDetailResponse
            {
                UserId = u.Id,
                FullName = u.FullName,
                Email = u.Email,
                PhoneNumber = u.PhoneNumber,
                AvatarUrl = u.AvatarUrl,
                AvatarMediaId = u.AvatarMediaId,
                StampsIssued = _context.Stamps.Count(s => s.AwardedByUserId == u.Id && s.Card.BusinessId == businessId),
                StampsToday = _context.StaffDailyAnalytics
                    .Where(a => a.StaffUserId == u.Id && a.BusinessId == businessId && a.Date == today)
                    .Sum(a => (int?)a.Stamps) ?? 0,
                StampsLast7d = _context.StaffDailyAnalytics
                    .Where(a => a.StaffUserId == u.Id && a.BusinessId == businessId && a.Date >= weekStart)
                    .Sum(a => (int?)a.Stamps) ?? 0,
                AppointmentsToday = _context.Appointments
                    .Count(a => a.StaffUserId == u.Id && a.BusinessId == businessId && a.ScheduledAt >= todayUtc && a.ScheduledAt < todayUtc.AddDays(1)),
                AppointmentsLast7d = _context.Appointments
                    .Count(a => a.StaffUserId == u.Id && a.BusinessId == businessId && a.ScheduledAt >= weekStartUtc),
                LastActivityAt = _context.Stamps
                    .Where(s => s.AwardedByUserId == u.Id && s.Card.BusinessId == businessId)
                    .Max(s => (DateTime?)s.StampedAt),
            })
            .FirstOrDefaultAsync();

        if (detail == null)
            return ApiResponse<StaffDetailResponse>.Fail("NOT_FOUND", "Staff member not found in your business.");

        // Employment + storefront profile come from the membership, which is the
        // row that carries them (the identity is shared across businesses).
        detail.MembershipStatus = membership.Status.ToString();
        detail.IsActive = membership.Status == BusinessMembershipStatus.Active;
        detail.JoinedAt = membership.JoinedAt;
        detail.EndedAt = membership.EndedAt;
        detail.PositionId = membership.PositionId;
        detail.Bio = membership.Bio;
        detail.Specialities = membership.Specialities;
        detail.YearsOfExperience = membership.YearsOfExperience;
        detail.IsPublic = membership.IsPublic;
        detail.IsBookable = membership.IsBookable;
        detail.DailyGoalOverride = user.DailyGoalOverride;
        detail.AppointmentDailyGoalOverride = user.AppointmentDailyGoalOverride;
        detail.DailyGoalType = business.DailyGoalType;
        detail.DailyGoal = business.DailyGoalType == "appointments"
            ? user.AppointmentDailyGoalOverride ?? business.DefaultAppointmentDailyGoal
            : user.DailyGoalOverride ?? business.DefaultDailyGoal;
        detail.AppointmentDailyGoal = user.AppointmentDailyGoalOverride ?? business.DefaultAppointmentDailyGoal;
        detail.BookingOpenMinute = business.BookingOpenHour * 60;
        detail.BookingCloseMinute = business.BookingCloseHour * 60;
        detail.TimeZoneId = business.TimeZoneId;

        if (membership.PositionId.HasValue)
        {
            detail.PositionName = await _context.StaffPositions.AsNoTracking()
                .Where(p => p.Id == membership.PositionId.Value && p.BusinessId == businessId)
                .Select(p => p.Name)
                .FirstOrDefaultAsync();
        }

        detail.Services = await LoadServicesAsync(businessId, staffUserId);
        var availability = await LoadAvailabilityAsync(businessId, staffUserId);
        detail.Availability = availability.Days;
        detail.AvailabilitySource = availability.Source;
        detail.TimeOff = await LoadTimeOffAsync(businessId, staffUserId);

        return ApiResponse<StaffDetailResponse>.Ok(detail);
    }
}



/// <summary>Profile and state writes.</summary>
public partial class StaffManagementService
{
    /// <inheritdoc />
    public async Task<ApiResponse<StaffDetailResponse>> UpdateStaffAsync(
        Guid callerUserId, string role, Guid staffUserId, UpdateStaffRequest request)
    {
        var resolved = await ResolveStaffAsync(callerUserId, role, staffUserId);
        if (resolved == null)
            return ApiResponse<StaffDetailResponse>.Fail("NOT_FOUND", "Staff member not found in your business.");

        var (businessId, membership, user) = resolved.Value;

        if (!await HasPermissionAsync(callerUserId, businessId, "staff.manage"))
            return ApiResponse<StaffDetailResponse>.Fail("FORBIDDEN", "You are not allowed to manage staff.");

        if (request.FullName != null)
        {
            var name = request.FullName.Trim();
            if (name.Length == 0)
                return ApiResponse<StaffDetailResponse>.Fail("VALIDATION_ERROR", "A name is required.");
            if (name.Length > 100)
                return ApiResponse<StaffDetailResponse>.Fail("VALIDATION_ERROR", "Name must not exceed 100 characters.");
            user.FullName = name;
        }

        if (request.PhoneNumber != null)
        {
            var phone = request.PhoneNumber.Trim();
            if (phone.Length > 20)
                return ApiResponse<StaffDetailResponse>.Fail("VALIDATION_ERROR", "Phone number must not exceed 20 characters.");
            user.PhoneNumber = phone.Length == 0 ? null : phone;
        }

        if (request.Bio != null)
        {
            var bio = request.Bio.Trim();
            if (bio.Length > 300)
                return ApiResponse<StaffDetailResponse>.Fail("VALIDATION_ERROR", "Biography must not exceed 300 characters.");
            membership.Bio = bio.Length == 0 ? null : bio;
        }

        // Specialities are free text the storefront splits on commas. Stored as
        // written; an empty string is a deliberate clear.
        if (request.Specialities != null)
        {
            var specialities = request.Specialities.Trim();
            if (specialities.Length > 300)
                return ApiResponse<StaffDetailResponse>.Fail("VALIDATION_ERROR", "Specialities must not exceed 300 characters.");
            membership.Specialities = specialities.Length == 0 ? null : specialities;
        }

        // Experience is nullable, so "clear" is an explicit instruction rather
        // than an absent field: omitting it must never silently erase a value.
        if (request.ClearYearsOfExperience)
        {
            membership.YearsOfExperience = null;
        }
        else if (request.YearsOfExperience.HasValue)
        {
            if (request.YearsOfExperience.Value < 0 || request.YearsOfExperience.Value > 80)
                return ApiResponse<StaffDetailResponse>.Fail("VALIDATION_ERROR", "Years of experience must be between 0 and 80.");
            membership.YearsOfExperience = request.YearsOfExperience.Value;
        }

        if (request.IsPublic.HasValue) membership.IsPublic = request.IsPublic.Value;
        if (request.IsBookable.HasValue) membership.IsBookable = request.IsBookable.Value;

        if (request.ClearDailyGoalOverride) user.DailyGoalOverride = null;
        else if (request.DailyGoalOverride.HasValue) user.DailyGoalOverride = NormalizeGoal(request.DailyGoalOverride);

        if (request.ClearAppointmentDailyGoalOverride) user.AppointmentDailyGoalOverride = null;
        else if (request.AppointmentDailyGoalOverride.HasValue)
            user.AppointmentDailyGoalOverride = NormalizeGoal(request.AppointmentDailyGoalOverride);

        // A profile photo is a media row: the media module owns the bytes, and the
        // resolved URL is derived from the delivered variants so the staff list can
        // render the photo without a second lookup.
        if (request.AvatarMediaId.HasValue && request.AvatarMediaId.Value != Guid.Empty)
        {
            var media = await _context.Media.AsNoTracking()
                .FirstOrDefaultAsync(m => m.Id == request.AvatarMediaId.Value && m.BusinessId == businessId);
            if (media == null)
                return ApiResponse<StaffDetailResponse>.Fail("NOT_FOUND", "That photo is not available for this business.");

            user.AvatarMediaId = media.Id;
            user.AvatarUrl = LargestVariantUrl(media.VariantsJson) ?? user.AvatarUrl;
        }

        // Position is descriptive metadata: changing it never grants anything.
        if (request.ClearPosition)
        {
            membership.PositionId = null;
        }
        else if (request.PositionId.HasValue)
        {
            var positionExists = await _context.StaffPositions.AsNoTracking()
                .AnyAsync(p => p.Id == request.PositionId.Value && p.BusinessId == businessId);
            if (!positionExists)
                return ApiResponse<StaffDetailResponse>.Fail("NOT_FOUND", "That position does not exist for this business.");

            membership.PositionId = request.PositionId.Value;
        }

        membership.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        _logger.LogInformation(
            "Staff profile updated: business {BusinessId} staff {StaffUserId} by {ActorUserId}",
            businessId, staffUserId, callerUserId);

        return await GetStaffDetailAsync(callerUserId, role, staffUserId);
    }

    /// <summary>
    /// The widest delivered variant URL for a media row, or null while the row is
    /// still processing. Mirrors how business logos resolve their public URL, so
    /// one media row is rendered the same way everywhere.
    /// </summary>
    private static string? LargestVariantUrl(string? variantsJson)
    {
        if (string.IsNullOrWhiteSpace(variantsJson)) return null;

        try
        {
            var variants = System.Text.Json.JsonSerializer.Deserialize<List<MediaVariantResponse>>(variantsJson) ?? [];
            return variants
                .Where(v => !string.IsNullOrWhiteSpace(v.Url))
                .OrderByDescending(v => v.Width)
                .Select(v => v.Url)
                .FirstOrDefault();
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}

/// <summary>Activate / deactivate (suspend) transitions.</summary>
public partial class StaffManagementService
{
    /// <inheritdoc />
    public async Task<ApiResponse<StaffStatusResponse>> SetStaffActiveAsync(
        Guid callerUserId, string role, Guid staffUserId, bool active)
    {
        var resolved = await ResolveStaffAsync(callerUserId, role, staffUserId);
        if (resolved == null)
            return ApiResponse<StaffStatusResponse>.Fail("NOT_FOUND", "Staff member not found in your business.");

        var (businessId, membership, user) = resolved.Value;

        if (!await HasPermissionAsync(callerUserId, businessId, "staff.manage"))
            return ApiResponse<StaffStatusResponse>.Fail("FORBIDDEN", "You are not allowed to manage staff.");

        // The owner's own membership is never suspended from the staff list:
        // doing so would lock the business out of its own console, and ownership
        // is transferred through the roles screen rather than the staff list.
        if (membership.Role == BusinessMembershipRole.Owner)
            return ApiResponse<StaffStatusResponse>.Fail(
                "FORBIDDEN", "The business owner cannot be deactivated from the staff list.");

        if (active)
        {
            if (membership.Status == BusinessMembershipStatus.Active)
                return ApiResponse<StaffStatusResponse>.Ok(BuildStatus(membership, "This staff member is already active."));

            membership.Status = BusinessMembershipStatus.Active;
            membership.EndedAt = null;
        }
        else
        {
            if (membership.Status != BusinessMembershipStatus.Active)
                return ApiResponse<StaffStatusResponse>.Ok(BuildStatus(membership, "This staff member is already inactive."));

            membership.Status = BusinessMembershipStatus.Suspended;
            membership.EndedAt = DateTime.UtcNow;
            // A suspended member stops being offered to customers immediately. The
            // owner re-enables bookability deliberately when they return, so a
            // returning member is never silently bookable before their schedule and
            // services have been reviewed.
            membership.IsBookable = false;
        }

        membership.UpdatedAt = DateTime.UtcNow;

        // Attendance sessions and events are deliberately untouched: they are the
        // attendance module's ledger, and rewriting a session without its CLOCK_OUT
        // event would leave that module's history internally inconsistent. The
        // console instead warns when a member is still clocked in and offers the
        // attendance module's own clock-out, which keeps the ledger honest.
        await _context.SaveChangesAsync();

        _logger.LogInformation(
            "Staff {Action}: business {BusinessId} staff {StaffUserId} by {ActorUserId}",
            active ? "activated" : "deactivated", businessId, staffUserId, callerUserId);

        return ApiResponse<StaffStatusResponse>.Ok(BuildStatus(
            membership,
            active
                ? $"{user.FullName} can take bookings again."
                : $"{user.FullName} is inactive. Their bookings, attendance and commission history are kept."));
    }

    private static StaffStatusResponse BuildStatus(BusinessMembership membership, string message) => new()
    {
        UserId = membership.UserId,
        MembershipStatus = membership.Status.ToString(),
        IsActive = membership.Status == BusinessMembershipStatus.Active,
        EndedAt = membership.EndedAt,
        Message = message
    };
}

/// <summary>Service assignments.</summary>
public partial class StaffManagementService
{
    /// <inheritdoc />
    public async Task<ApiResponse<List<StaffServiceLinkResponse>>> GetStaffServicesAsync(
        Guid callerUserId, string role, Guid staffUserId)
    {
        var resolved = await ResolveStaffAsync(callerUserId, role, staffUserId);
        if (resolved == null)
            return ApiResponse<List<StaffServiceLinkResponse>>.Fail("NOT_FOUND", "Staff member not found in your business.");

        if (!await HasPermissionAsync(callerUserId, resolved.Value.BusinessId, "staff.view"))
            return ApiResponse<List<StaffServiceLinkResponse>>.Fail("FORBIDDEN", "You are not allowed to view staff.");

        return ApiResponse<List<StaffServiceLinkResponse>>.Ok(
            await LoadServicesAsync(resolved.Value.BusinessId, staffUserId));
    }

    /// <inheritdoc />
    public async Task<ApiResponse<List<StaffServiceLinkResponse>>> SetStaffServicesAsync(
        Guid callerUserId, string role, Guid staffUserId, SetStaffServicesRequest request)
    {
        var resolved = await ResolveStaffAsync(callerUserId, role, staffUserId);
        if (resolved == null)
            return ApiResponse<List<StaffServiceLinkResponse>>.Fail("NOT_FOUND", "Staff member not found in your business.");

        var businessId = resolved.Value.BusinessId;

        if (!await HasPermissionAsync(callerUserId, businessId, "staff.manage"))
            return ApiResponse<List<StaffServiceLinkResponse>>.Fail("FORBIDDEN", "You are not allowed to manage staff.");

        var requested = (request.ServiceIds ?? []).Where(id => id != Guid.Empty).Distinct().ToList();

        // Every id must belong to THIS business. A foreign id is rejected outright
        // rather than silently dropped, so a client cannot half-succeed.
        if (requested.Count > 0)
        {
            var owned = await _context.ServiceCatalogItems.AsNoTracking()
                .Where(s => s.BusinessId == businessId && requested.Contains(s.Id))
                .Select(s => s.Id)
                .ToListAsync();

            var foreign = requested.Except(owned).ToList();
            if (foreign.Count > 0)
                return ApiResponse<List<StaffServiceLinkResponse>>.Fail(
                    "NOT_FOUND", "One or more of those services are not available for this business.");
        }

        var existing = await _context.StaffServiceAssignments
            .Where(a => a.BusinessId == businessId && a.StaffUserId == staffUserId)
            .ToListAsync();

        var toRemove = existing.Where(a => !requested.Contains(a.ServiceCatalogItemId)).ToList();
        var existingIds = existing.Select(a => a.ServiceCatalogItemId).ToHashSet();
        var toAdd = requested.Where(id => !existingIds.Contains(id))
            .Select(id => new StaffServiceAssignment
            {
                Id = Guid.NewGuid(),
                BusinessId = businessId,
                StaffUserId = staffUserId,
                ServiceCatalogItemId = id,
                CreatedAt = DateTime.UtcNow
            })
            .ToList();

        // One transaction: a member is never left assigned to a service the client
        // believed it had removed, and never missing one it believed it had added.
        if (toRemove.Count > 0 || toAdd.Count > 0)
        {
            await using var transaction = await _context.Database.BeginTransactionAsync();
            if (toRemove.Count > 0) _context.StaffServiceAssignments.RemoveRange(toRemove);
            if (toAdd.Count > 0) await _context.StaffServiceAssignments.AddRangeAsync(toAdd);
            await _context.SaveChangesAsync();
            await transaction.CommitAsync();

            _logger.LogInformation(
                "Staff services updated: business {BusinessId} staff {StaffUserId} added {Added} removed {Removed} by {ActorUserId}",
                businessId, staffUserId, toAdd.Count, toRemove.Count, callerUserId);
        }

        return ApiResponse<List<StaffServiceLinkResponse>>.Ok(
            await LoadServicesAsync(businessId, staffUserId));
    }
}


/// <summary>Working week, dated time off and their readers.</summary>
public partial class StaffManagementService
{
    /// <inheritdoc />
    public async Task<ApiResponse<List<StaffAvailabilityDayResponse>>> GetStaffAvailabilityAsync(
        Guid callerUserId, string role, Guid staffUserId)
    {
        var resolved = await ResolveStaffAsync(callerUserId, role, staffUserId);
        if (resolved == null)
            return ApiResponse<List<StaffAvailabilityDayResponse>>.Fail("NOT_FOUND", "Staff member not found in your business.");

        if (!await HasPermissionAsync(callerUserId, resolved.Value.BusinessId, "staff.view"))
            return ApiResponse<List<StaffAvailabilityDayResponse>>.Fail("FORBIDDEN", "You are not allowed to view staff.");

        return ApiResponse<List<StaffAvailabilityDayResponse>>.Ok(
            (await LoadAvailabilityAsync(resolved.Value.BusinessId, staffUserId)).Days);
    }

    /// <inheritdoc />
    public async Task<ApiResponse<List<StaffAvailabilityDayResponse>>> SetStaffAvailabilityAsync(
        Guid callerUserId, string role, Guid staffUserId, SetStaffAvailabilityRequest request)
    {
        var resolved = await ResolveStaffAsync(callerUserId, role, staffUserId);
        if (resolved == null)
            return ApiResponse<List<StaffAvailabilityDayResponse>>.Fail("NOT_FOUND", "Staff member not found in your business.");

        var businessId = resolved.Value.BusinessId;

        if (!await HasPermissionAsync(callerUserId, businessId, "staff.manage"))
            return ApiResponse<List<StaffAvailabilityDayResponse>>.Fail("FORBIDDEN", "You are not allowed to manage staff.");

        var days = request.Days ?? [];
        if (days.Count == 0)
            return ApiResponse<List<StaffAvailabilityDayResponse>>.Fail(
                "VALIDATION_ERROR", "Provide at least one weekday.");

        if (days.Select(d => d.DayOfWeek).Distinct().Count() != days.Count)
            return ApiResponse<List<StaffAvailabilityDayResponse>>.Fail(
                "VALIDATION_ERROR", "Each weekday may only appear once.");

        foreach (var day in days)
        {
            if (day.DayOfWeek is < 0 or > 6)
                return ApiResponse<List<StaffAvailabilityDayResponse>>.Fail(
                    "VALIDATION_ERROR", "dayOfWeek must be between 0 (Sunday) and 6 (Saturday).");

            var error = ValidateWindow(day.IsWorking, day.StartMinute, day.EndMinute,
                day.BreakStartMinute, day.BreakEndMinute);
            if (error != null)
                return ApiResponse<List<StaffAvailabilityDayResponse>>.Fail("VALIDATION_ERROR", error);
        }

        var existing = await _context.StaffAvailability
            .Where(a => a.BusinessId == businessId && a.StaffUserId == staffUserId)
            .ToListAsync();

        var byDay = existing.ToDictionary(a => a.DayOfWeek);

        await using var transaction = await _context.Database.BeginTransactionAsync();

        foreach (var day in days)
        {
            var (start, end, breakStart, breakEnd) = NormalizeWindow(
                day.IsWorking, day.StartMinute, day.EndMinute, day.BreakStartMinute, day.BreakEndMinute);

            if (byDay.TryGetValue(day.DayOfWeek, out var row))
            {
                row.IsWorking = day.IsWorking;
                row.StartMinute = start;
                row.EndMinute = end;
                row.BreakStartMinute = breakStart;
                row.BreakEndMinute = breakEnd;
                row.UpdatedAt = DateTime.UtcNow;
            }
            else
            {
                await _context.StaffAvailability.AddAsync(new StaffAvailability
                {
                    Id = Guid.NewGuid(),
                    BusinessId = businessId,
                    StaffUserId = staffUserId,
                    DayOfWeek = day.DayOfWeek,
                    IsWorking = day.IsWorking,
                    StartMinute = start,
                    EndMinute = end,
                    BreakStartMinute = breakStart,
                    BreakEndMinute = breakEnd,
                    UpdatedAt = DateTime.UtcNow,
                    CreatedAt = DateTime.UtcNow
                });
            }
        }

        await _context.SaveChangesAsync();
        await transaction.CommitAsync();

        _logger.LogInformation(
            "Staff working week updated: business {BusinessId} staff {StaffUserId} days {Days} by {ActorUserId}",
            businessId, staffUserId, days.Count, callerUserId);

        return ApiResponse<List<StaffAvailabilityDayResponse>>.Ok(
            (await LoadAvailabilityAsync(businessId, staffUserId)).Days);
    }

    /// <inheritdoc />
    public async Task<ApiResponse<List<StaffAvailabilityDayResponse>>> ClearStaffAvailabilityAsync(
        Guid callerUserId, string role, Guid staffUserId)
    {
        var resolved = await ResolveStaffAsync(callerUserId, role, staffUserId);
        if (resolved == null)
            return ApiResponse<List<StaffAvailabilityDayResponse>>.Fail("NOT_FOUND", "Staff member not found in your business.");

        var businessId = resolved.Value.BusinessId;

        if (!await HasPermissionAsync(callerUserId, businessId, "staff.manage"))
            return ApiResponse<List<StaffAvailabilityDayResponse>>.Fail("FORBIDDEN", "You are not allowed to manage staff.");

        var rows = await _context.StaffAvailability
            .Where(a => a.BusinessId == businessId && a.StaffUserId == staffUserId)
            .ToListAsync();

        if (rows.Count > 0)
        {
            _context.StaffAvailability.RemoveRange(rows);
            await _context.SaveChangesAsync();

            _logger.LogInformation(
                "Staff working week cleared: business {BusinessId} staff {StaffUserId} by {ActorUserId}",
                businessId, staffUserId, callerUserId);
        }

        // The read after a clear is the derived week, which is what the editor will
        // show next — so the response is already the truth the UI should render.
        return ApiResponse<List<StaffAvailabilityDayResponse>>.Ok(
            (await LoadAvailabilityAsync(businessId, staffUserId)).Days);
    }

    /// <summary>
    /// Validates one working window. Returns null when the window is coherent, else
    /// the message the caller should see — the same rule the database enforces.
    ///
    /// <para>A day that is switched off needs no window at all: its times are
    /// ignored (and normalised away on write), so "off" is expressed by the switch
    /// rather than by a 00:00–00:00 range.</para>
    /// </summary>
    private static string? ValidateWindow(
        bool isWorking, int startMinute, int endMinute, int? breakStart, int? breakEnd)
    {
        if (startMinute is < 0 or > 1439)
            return "The start time must be between 00:00 and 23:59.";
        if (endMinute is < 0 or > 1440)
            return "The end time must be between 00:00 and 24:00.";

        if (!isWorking) return null;

        if (endMinute <= startMinute)
            return "The end time must be after the start time.";

        if (breakStart.HasValue != breakEnd.HasValue)
            return "A break needs both a start and an end.";

        if (breakStart.HasValue && breakEnd.HasValue)
        {
            if (breakEnd.Value <= breakStart.Value)
                return "The break must end after it starts.";
            if (breakStart.Value < startMinute || breakEnd.Value > endMinute)
                return "The break must sit inside the working hours.";
        }

        return null;
    }

    /// <summary>
    /// The window actually persisted for a weekday. A day off is stored as a
    /// full-day placeholder (the database requires an end time on every row) and is
    /// reported back as an empty window, so the switch — not the numbers — is what
    /// says "closed".
    /// </summary>
    private static (int Start, int End, int? BreakStart, int? BreakEnd) NormalizeWindow(
        bool isWorking, int startMinute, int endMinute, int? breakStart, int? breakEnd)
    {
        if (!isWorking) return (0, 1440, null, null);
        return (startMinute, endMinute, breakStart, breakEnd);
    }
}


/// <summary>Batched readers shared by the profile and the relation endpoints.</summary>
public partial class StaffManagementService
{
    /// <summary>
    /// The services a member delivers, with their category and price/duration
    /// snapshot. Two batched queries — never one per assignment.
    /// </summary>
    private async Task<List<StaffServiceLinkResponse>> LoadServicesAsync(Guid businessId, Guid staffUserId)
    {
        var serviceIds = await _context.StaffServiceAssignments.AsNoTracking()
            .Where(a => a.BusinessId == businessId && a.StaffUserId == staffUserId)
            .Select(a => a.ServiceCatalogItemId)
            .ToListAsync();

        if (serviceIds.Count == 0) return [];

        var services = await _context.ServiceCatalogItems.AsNoTracking()
            .Where(s => s.BusinessId == businessId && serviceIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Name, s.CategoryId, s.DurationMinutes, s.Price, s.IsActive, s.Showcase })
            .ToListAsync();

        var categoryIds = services.Where(s => s.CategoryId.HasValue)
            .Select(s => s.CategoryId!.Value).Distinct().ToList();

        var categories = categoryIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _context.ServiceCategories.AsNoTracking()
                .Where(c => c.BusinessId == businessId && categoryIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, c => c.Name);

        return services
            .OrderBy(s => s.Name)
            .Select(s => new StaffServiceLinkResponse
            {
                ServiceId = s.Id,
                Name = s.Name,
                CategoryId = s.CategoryId,
                CategoryName = s.CategoryId.HasValue && categories.TryGetValue(s.CategoryId.Value, out var name)
                    ? name
                    : null,
                DurationMinutes = s.DurationMinutes,
                Price = s.Price,
                IsActive = s.IsActive,
                Showcase = s.Showcase
            })
            .ToList();
    }

    /// <summary>
    /// The recurring working week, always in Sunday→Saturday order with all seven
    /// days present.
    ///
    /// <para>When no pattern has been saved yet, the week is <b>derived from the
    /// dated shifts already on the calendar</b> — otherwise the editor would show an
    /// empty week for a member whose hours are demonstrably in effect, and saving
    /// that empty week would silently clear their roster. The source is returned so
    /// the UI can say which of the three it is showing.</para>
    /// </summary>
    private async Task<(List<StaffAvailabilityDayResponse> Days, string Source)> LoadAvailabilityAsync(
        Guid businessId, Guid staffUserId)
    {
        var rows = await _context.StaffAvailability.AsNoTracking()
            .Where(a => a.BusinessId == businessId && a.StaffUserId == staffUserId)
            .ToListAsync();

        if (rows.Count > 0)
        {
            var byDay = rows.ToDictionary(a => a.DayOfWeek);
            return (BuildWeek(day =>
            {
                if (!byDay.TryGetValue(day, out var row)) return null;
                return (row.IsWorking, row.StartMinute, row.EndMinute, row.BreakStartMinute, row.BreakEndMinute);
            }), "pattern");
        }

        // Derive from the roster: the most common window per weekday over the next
        // four weeks, so one stray override cannot redefine the week.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var horizon = today.AddDays(27);

        var rostered = await _context.StaffShifts.AsNoTracking()
            .Where(s => s.BusinessId == businessId && s.StaffUserId == staffUserId
                && s.Date >= today && s.Date <= horizon)
            .ToListAsync();

        if (rostered.Count == 0)
            return (BuildWeek(_ => null), "business");

        var byWeekday = rostered
            .GroupBy(s => (int)s.Date.DayOfWeek)
            .ToDictionary(
                g => g.Key,
                g => g
                    .Select(s => (
                        s.IsWorking,
                        Start: s.StartMinute ?? s.StartHour * 60,
                        End: s.EndMinute ?? s.EndHour * 60,
                        s.BreakStartMinute,
                        s.BreakEndMinute))
                    .GroupBy(w => w)
                    .OrderByDescending(group => group.Count())
                    .First()
                    .Key);

        return (BuildWeek(day =>
            byWeekday.TryGetValue(day, out var window) ? window : ((bool, int, int, int?, int?)?)null),
            "roster");
    }

    /// <summary>Assembles the seven-day week, filling unset days from the projector.</summary>
    private static List<StaffAvailabilityDayResponse> BuildWeek(
        Func<int, (bool IsWorking, int StartMinute, int EndMinute, int? BreakStart, int? BreakEnd)?> project)
    {
        return Enumerable.Range(0, 7).Select(day =>
        {
            var window = project(day);
            var working = window?.IsWorking ?? false;

            return new StaffAvailabilityDayResponse
            {
                DayOfWeek = day,
                DayName = DayNames[day],
                IsWorking = working,
                // A closed day reports an empty window: the stored placeholder is an
                // implementation detail of the database's NOT NULL end time.
                StartMinute = working ? window?.StartMinute ?? 0 : 0,
                EndMinute = working ? window?.EndMinute ?? 0 : 0,
                BreakStartMinute = working ? window?.BreakStart : null,
                BreakEndMinute = working ? window?.BreakEnd : null
            };
        }).ToList();
    }

    /// <summary>
    /// Dated EXCEPTIONS in the horizon: a day off, or a one-off change that carries a
    /// reason. A plain working day is not an exception — it is the roster — so it is
    /// not listed here, which keeps the panel meaningful instead of echoing the
    /// calendar back at the owner.
    /// </summary>
    private async Task<List<StaffTimeOffResponse>> LoadTimeOffAsync(Guid businessId, Guid staffUserId)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var horizon = today.AddDays(TimeOffHorizonDays);

        return await _context.StaffShifts.AsNoTracking()
            .Where(s => s.BusinessId == businessId && s.StaffUserId == staffUserId
                && s.Date >= today && s.Date <= horizon
                && (!s.IsWorking || s.Note != null))
            .OrderBy(s => s.Date)
            .Select(s => new StaffTimeOffResponse
            {
                Date = s.Date,
                IsWorking = s.IsWorking,
                StartMinute = s.StartMinute ?? s.StartHour * 60,
                EndMinute = s.EndMinute ?? s.EndHour * 60,
                Reason = s.Note
            })
            .ToListAsync();
    }
}

/// <summary>Dated time off: the exception that overrides the recurring week.</summary>
public partial class StaffManagementService
{
    /// <inheritdoc />
    public async Task<ApiResponse<List<StaffTimeOffResponse>>> AddStaffTimeOffAsync(
        Guid callerUserId, string role, Guid staffUserId, AddStaffTimeOffRequest request)
    {
        var resolved = await ResolveStaffAsync(callerUserId, role, staffUserId);
        if (resolved == null)
            return ApiResponse<List<StaffTimeOffResponse>>.Fail("NOT_FOUND", "Staff member not found in your business.");

        var businessId = resolved.Value.BusinessId;

        if (!await HasPermissionAsync(callerUserId, businessId, "staff.manage"))
            return ApiResponse<List<StaffTimeOffResponse>>.Fail("FORBIDDEN", "You are not allowed to manage staff.");

        if (request.To < request.From)
            return ApiResponse<List<StaffTimeOffResponse>>.Fail(
                "VALIDATION_ERROR", "The end date must be on or after the start date.");

        var span = request.To.DayNumber - request.From.DayNumber;
        if (span > TimeOffHorizonDays)
            return ApiResponse<List<StaffTimeOffResponse>>.Fail(
                "VALIDATION_ERROR", $"Time off can cover at most {TimeOffHorizonDays} days at a time.");

        var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
        if (reason != null && reason.Length > 200)
            return ApiResponse<List<StaffTimeOffResponse>>.Fail(
                "VALIDATION_ERROR", "The reason must not exceed 200 characters.");

        // One row per date, upserted: a day already marked off keeps its place and
        // simply picks up the new reason. IsWorking=false is what makes the booking
        // engine treat the date as unavailable.
        var dates = Enumerable.Range(0, span + 1).Select(offset => request.From.AddDays(offset)).ToList();
        var existing = await _context.StaffShifts
            .Where(s => s.BusinessId == businessId && s.StaffUserId == staffUserId && dates.Contains(s.Date))
            .ToListAsync();

        var byDate = existing.ToDictionary(s => s.Date);

        await using var transaction = await _context.Database.BeginTransactionAsync();

        foreach (var date in dates)
        {
            if (byDate.TryGetValue(date, out var row))
            {
                row.IsWorking = false;
                row.StartHour = 0;
                row.EndHour = 0;
                // Left NULL on purpose: the minute columns are an optional refinement,
                // and the database only allows a real end time on a working row.
                row.StartMinute = null;
                row.EndMinute = null;
                row.BreakStartMinute = null;
                row.BreakEndMinute = null;
                row.Note = reason;
            }
            else
            {
                await _context.StaffShifts.AddAsync(new StaffShift
                {
                    Id = Guid.NewGuid(),
                    BusinessId = businessId,
                    StaffUserId = staffUserId,
                    Date = date,
                    StartHour = 0,
                    EndHour = 0,
                    StartMinute = null,
                    EndMinute = null,
                    IsWorking = false,
                    Note = reason,
                    CreatedAt = DateTime.UtcNow
                });
            }
        }

        await _context.SaveChangesAsync();
        await transaction.CommitAsync();

        _logger.LogInformation(
            "Staff time off added: business {BusinessId} staff {StaffUserId} {From}..{To} by {ActorUserId}",
            businessId, staffUserId, request.From, request.To, callerUserId);

        return ApiResponse<List<StaffTimeOffResponse>>.Ok(
            await LoadTimeOffAsync(businessId, staffUserId));
    }

    /// <inheritdoc />
    public async Task<ApiResponse<List<StaffTimeOffResponse>>> RemoveStaffTimeOffAsync(
        Guid callerUserId, string role, Guid staffUserId, DateOnly date)
    {
        var resolved = await ResolveStaffAsync(callerUserId, role, staffUserId);
        if (resolved == null)
            return ApiResponse<List<StaffTimeOffResponse>>.Fail("NOT_FOUND", "Staff member not found in your business.");

        var businessId = resolved.Value.BusinessId;

        if (!await HasPermissionAsync(callerUserId, businessId, "staff.manage"))
            return ApiResponse<List<StaffTimeOffResponse>>.Fail("FORBIDDEN", "You are not allowed to manage staff.");

        var row = await _context.StaffShifts
            .FirstOrDefaultAsync(s => s.BusinessId == businessId && s.StaffUserId == staffUserId && s.Date == date);

        if (row == null)
            return ApiResponse<List<StaffTimeOffResponse>>.Fail("NOT_FOUND", "No override exists for that date.");

        // Removing the dated row hands the date back to the recurring week, which
        // is the opposite of deleting anything the customer could have booked.
        _context.StaffShifts.Remove(row);
        await _context.SaveChangesAsync();

        return ApiResponse<List<StaffTimeOffResponse>>.Ok(
            await LoadTimeOffAsync(businessId, staffUserId));
    }
}
