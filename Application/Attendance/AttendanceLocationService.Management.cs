using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.Attendance.Verification;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Application.Attendance;

/// <summary>
/// Phase 4 — owner settings + overview + per-staff history (plan §13.3, §15.2).
/// These orchestration methods live on the same partial class as the location/QR
/// lifecycle so every owner-scoped query reuses the single server-side business
/// resolver (<see cref="ResolveBusinessIdAsync"/>) and audit helper.
/// </summary>
public partial class AttendanceLocationService
{
    /// <summary>
    /// Effective settings: policy fields + derived readiness counts. A missing
    /// policy row surfaces the implicit Standard default (plan §6.1) so a
    /// business that never opened settings still reads working values.
    /// </summary>
    public async Task<ApiResponse<AttendanceSettingsResponse>> GetSettingsAsync(Guid ownerUserId)
    {
        var businessId = await ResolveBusinessIdAsync(ownerUserId);
        if (businessId == null)
            return ApiResponse<AttendanceSettingsResponse>.Fail("NOT_FOUND", "Business not found.");

        var policy = await GetPolicyRowAsync(businessId.Value);
        var (locationCount, activeCredentialCount) = await GetReadinessAsync(businessId.Value);

        return ApiResponse<AttendanceSettingsResponse>.Ok(BuildSettingsResponse(
            policy, isConfigured: locationCount > 0, locationCount, activeCredentialCount));
    }

    /// <summary>
    /// PUT settings — validates the whole-policy payload (INVALID_MODE /
    /// INVALID_VERIFICATION_SET / INVALID_SESSION_WINDOW) and writes the policy
    /// row, creating it lazily when none exists (plan §6.1, §15.2). MaxOpenSessionHours
    /// is validated here for consistency; V1 keeps it as the in-memory default
    /// constant (no persisted column — see plan §6.1 table).
    /// </summary>
    public async Task<ApiResponse<AttendanceSettingsResponse>> UpdateSettingsAsync(
        Guid ownerUserId, AttendanceSettingsRequest request)
    {
        var businessId = await ResolveBusinessIdAsync(ownerUserId);
        if (businessId == null)
            return ApiResponse<AttendanceSettingsResponse>.Fail("NOT_FOUND", "Business not found.");

        var modeError = AttendancePolicyService.ValidateMode(request.Mode);
        if (modeError != null)
            return ApiResponse<AttendanceSettingsResponse>.Fail(modeError, "Unknown attendance mode.");

        var verificationError = AttendancePolicyService.ValidateVerificationSet(request.RequiredVerifications);
        if (verificationError != null)
            return ApiResponse<AttendanceSettingsResponse>.Fail(
                verificationError,
                "requiredVerifications must be a non-empty set including AUTHENTICATED_USER.");

        var windowError = AttendancePolicyService.ValidateSessionWindow(request.MaxOpenSessionHours);
        if (windowError != null)
            return ApiResponse<AttendanceSettingsResponse>.Fail(
                windowError, "maxOpenSessionHours must be between 4 and 48.");

        var now = DateTime.UtcNow;
        var before = await GetPolicyRowAsync(businessId.Value);

        var policy = await _context.AttendancePolicies
            .FirstOrDefaultAsync(p => p.BusinessId == businessId.Value);

        if (policy == null)
        {
            policy = new AttendancePolicy
            {
                Id = Guid.NewGuid(),
                BusinessId = businessId.Value,
                CreatedAt = now,
            };
            _context.AttendancePolicies.Add(policy);
        }

        var parsedMode = AttendanceVerification.ParseModeWireValue(request.Mode)
                         ?? AttendanceMode.Standard;
        policy.Mode = parsedMode;
        policy.RequiredVerificationsJson =
            AttendancePolicyService.SerializeVerifications(request.RequiredVerifications);
        policy.IsActive = request.IsActive;
        policy.UpdatedAt = now;

        await _context.SaveChangesAsync();

        await AuditAsync(AttendanceAuditActions.SettingsUpdated, businessId.Value, ownerUserId,
            "PUT /v1/businesses/me/attendance/settings",
            details: new
            {
                before = MapPolicyWire(before),
                after = MapPolicyWire(policy),
            });

                var (locationCount, activeCredentialCount) = await GetReadinessAsync(businessId.Value);
        return ApiResponse<AttendanceSettingsResponse>.Ok(BuildSettingsResponse(
            policy, isConfigured: locationCount > 0, locationCount, activeCredentialCount));
    }

    // ── Phase 4 helpers ──────────────────────────────────────────────

    private async Task<(int locationCount, int activeCredentialCount)> GetReadinessAsync(Guid businessId)
    {
        var locationCount = await _context.AttendanceLocations.CountAsync(l => l.BusinessId == businessId);
        var activeCredentialCount = await _context.AttendanceQrCredentials
            .CountAsync(c => c.BusinessId == businessId && c.Status == AttendanceCredentialStatus.Active);
        return (locationCount, activeCredentialCount);
    }

    private static AttendanceSettingsResponse BuildSettingsResponse(
        AttendancePolicy policy, bool isConfigured, int locationCount, int activeCredentialCount)
    {
        var methods = AttendanceVerification.ParseRequiredMethods(policy.RequiredVerificationsJson)
            .OrderBy(m => (int)m)
            .Select(AttendanceVerification.WireValue)
            .ToArray();

        return new AttendanceSettingsResponse
        {
            IsActive = policy.IsActive,
            Mode = AttendanceVerification.WireValue(policy.Mode),
            RequiredVerifications = methods,
            MaxOpenSessionHours = AttendancePolicyService.DefaultMaxOpenSessionHours,
            IsConfigured = isConfigured,
            LocationCount = locationCount,
            ActiveCredentialCount = activeCredentialCount,
            UpdatedAt = policy.UpdatedAt,
        };
    }

    private async Task<AttendancePolicy> GetPolicyRowAsync(Guid businessId) =>
        await _context.AttendancePolicies.AsNoTracking()
            .FirstOrDefaultAsync(p => p.BusinessId == businessId)
        ?? AttendancePolicyService.CreateImplicitDefault(businessId);

    private static object MapPolicyWire(AttendancePolicy policy) => new
    {
        mode = AttendanceVerification.WireValue(policy.Mode),
        requiredVerifications = AttendanceVerification.ParseRequiredMethods(policy.RequiredVerificationsJson)
            .OrderBy(m => (int)m).Select(AttendanceVerification.WireValue).ToArray(),
        isActive = policy.IsActive,
        maxOpenSessionHours = AttendancePolicyService.DefaultMaxOpenSessionHours,
    };

    /// <summary>Whole-minute overlap of a session [start,end) with a day window.</summary>
    private static int OverlapMinutes(DateTime? start, DateTime? end, DateTime dayStart, DateTime dayEnd)
    {
        if (!start.HasValue) return 0;
        var s = start.Value;
        var e = end ?? DateTime.UtcNow;
        var overlap = (Math.Min(e.Ticks, dayEnd.Ticks) - Math.Max(s.Ticks, dayStart.Ticks));
                return overlap < 0 ? 0 : (int)(overlap / TimeSpan.TicksPerMinute);
    }

    /// <summary>
    /// Manager "who is in right now" overview (plan §13.3). "Today" is
    /// business-local wall-clock via <see cref="Business.TimeZoneId"/>.
    /// </summary>
    public async Task<ApiResponse<AttendanceOverviewResponse>> GetOverviewAsync(
        Guid ownerUserId, DateOnly? date = null)
    {
        var businessId = await ResolveBusinessIdAsync(ownerUserId);
        if (businessId == null)
            return ApiResponse<AttendanceOverviewResponse>.Fail("NOT_FOUND", "Business not found.");

        var tzId = await _context.Businesses
            .Where(b => b.Id == businessId.Value).Select(b => b.TimeZoneId)
            .FirstOrDefaultAsync() ?? "Africa/Nairobi";

        TimeZoneInfo tz;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById(tzId); }
        catch { tz = TimeZoneInfo.Utc; }

        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
        var day = date ?? DateOnly.FromDateTime(nowLocal.Date);
        var dayStartUtc = TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue), tz);
        var dayEndUtc = dayStartUtc.AddDays(1);

        var staff = await _context.Users.AsNoTracking()
            .Where(u => u.StaffBusinessId == businessId.Value && !u.IsDeleted)
            .Select(u => new { u.Id, u.FullName }).ToListAsync();

        var staffIds = staff.Select(s => s.Id).ToList();
        var openSessions = await _context.AttendanceSessions.AsNoTracking()
            .Where(s => s.BusinessId == businessId.Value && s.ClosedAt == null
                        && staffIds.Contains(s.StaffUserId))
            .ToDictionaryAsync(s => s.StaffUserId);

        var todaysClosed = await _context.AttendanceSessions.AsNoTracking()
            .Where(s => s.BusinessId == businessId.Value && s.OpenedAt < dayEndUtc
                        && (s.ClosedAt == null || s.ClosedAt > dayStartUtc)
                        && staffIds.Contains(s.StaffUserId))
            .Select(s => new { s.StaffUserId, s.OpenedAt, s.ClosedAt, s.WorkedMinutes, s.OpeningLocationId })
            .ToListAsync();

        var locationIds = openSessions.Values
            .Select(s => s.OpeningLocationId).Where(id => id.HasValue)
            .Select(id => id!.Value).Distinct().ToList();
        var locationNames = locationIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _context.AttendanceLocations.AsNoTracking()
                .Where(l => locationIds.Contains(l.Id))
                .ToDictionaryAsync(l => l.Id, l => l.Name);

        var overview = new AttendanceOverviewResponse { Date = day };
        var totalWorked = 0;

        foreach (var member in staff)
        {
            if (openSessions.TryGetValue(member.Id, out var open))
            {
                overview.Staff.Add(new AttendanceOverviewStaffItem
                {
                    StaffUserId = member.Id,
                    FullName = member.FullName,
                    State = "CLOCKED_IN",
                    OpenedAt = open.OpenedAt,
                    WorkedMinutes = OverlapMinutes(open.OpenedAt, null, dayStartUtc, dayEndUtc),
                    LocationName = open.OpeningLocationId.HasValue
                        && locationNames.TryGetValue(open.OpeningLocationId.Value, out var loc) ? loc : null,
                });
                overview.ClockedInCount++;
            }
            else
            {
                var memberClosed = todaysClosed.Where(s => s.StaffUserId == member.Id).ToList();
                var minutes = (int)memberClosed.Sum(s =>
                    s.WorkedMinutes ?? OverlapMinutes(s.OpenedAt, s.ClosedAt, dayStartUtc, dayEndUtc));
                overview.Staff.Add(new AttendanceOverviewStaffItem
                {
                    StaffUserId = member.Id,
                    FullName = member.FullName,
                    State = memberClosed.Count > 0 ? "CLOCKED_OUT" : "NOT_CLOCKED_IN",
                    WorkedMinutes = minutes > 0 ? minutes : null,
                });
                totalWorked += minutes;
                overview.NotClockedInCount++;
            }
        }

                overview.TotalWorkedMinutes = totalWorked;
        return ApiResponse<AttendanceOverviewResponse>.Ok(overview);
    }

    /// <summary>
    /// Per-staff drill-down history (plan §13.3). Staff id is business-scoped
    /// (WHERE StaffBusinessId == scoped business) so a foreign id answers
    /// STAFF_NOT_FOUND — never an enumeration leak.
    /// </summary>
    public async Task<ApiResponse<PaginatedResponse<AttendanceHistoryItem>>> GetStaffHistoryAsync(
        Guid ownerUserId, Guid staffUserId, AttendanceHistoryQuery query)
    {
        var businessId = await ResolveBusinessIdAsync(ownerUserId);
        if (businessId == null)
            return ApiResponse<PaginatedResponse<AttendanceHistoryItem>>.Fail("NOT_FOUND", "Business not found.");

        var linked = await _context.Users.AsNoTracking()
            .AnyAsync(u => u.Id == staffUserId && u.StaffBusinessId == businessId.Value && !u.IsDeleted);
        if (!linked)
            return ApiResponse<PaginatedResponse<AttendanceHistoryItem>>.Fail(
                "STAFF_NOT_FOUND", "Staff member not found.");

        query ??= new AttendanceHistoryQuery();
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);

        if (query.From.HasValue && query.To.HasValue && query.From.Value > query.To.Value)
            return ApiResponse<PaginatedResponse<AttendanceHistoryItem>>.Fail(
                "INVALID_DATE_RANGE", "from must not be after to.");

        var baseQuery = _context.AttendanceEvents.AsNoTracking()
            .Where(e => e.BusinessId == businessId.Value && e.StaffUserId == staffUserId);

        if (query.LocationId.HasValue)
            baseQuery = baseQuery.Where(e => e.AttendanceLocationId == query.LocationId.Value);
        if (query.From.HasValue)
            baseQuery = baseQuery.Where(e => e.OccurredAt >= query.From.Value.ToDateTime(TimeOnly.MinValue));
        if (query.To.HasValue)
            baseQuery = baseQuery.Where(e => e.OccurredAt < query.To.Value.AddDays(1).ToDateTime(TimeOnly.MinValue));

        var total = await baseQuery.LongCountAsync();
        var events = await baseQuery.OrderByDescending(e => e.OccurredAt)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();

        var sessionIds = events.Where(e => e.AttendanceSessionId.HasValue)
            .Select(e => e.AttendanceSessionId!.Value).Distinct().ToList();
        var sessions = sessionIds.Count == 0
            ? new Dictionary<Guid, AttendanceSession>()
            : await _context.AttendanceSessions.AsNoTracking()
                .Where(s => sessionIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id);

        var locationIds = events.Where(e => e.AttendanceLocationId.HasValue)
            .Select(e => e.AttendanceLocationId!.Value).Distinct().ToList();
        var locations = locationIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _context.AttendanceLocations.AsNoTracking()
                .Where(l => locationIds.Contains(l.Id))
                .ToDictionaryAsync(l => l.Id, l => l.Name);

        var items = events.Select(e =>
        {
            sessions.TryGetValue(e.AttendanceSessionId ?? Guid.Empty, out var session);
            locations.TryGetValue(e.AttendanceLocationId ?? Guid.Empty, out var locationName);
            var open = session != null && session.ClosedAt == null;
            return new AttendanceHistoryItem
            {
                Id = e.Id,
                EventType = AttendanceVerification.WireValue(e.EventType),
                OccurredAt = e.OccurredAt,
                LocationId = e.AttendanceLocationId,
                LocationName = locationName,
                SessionId = e.AttendanceSessionId,
                WorkedMinutes = session == null || open ? null : session.WorkedMinutes,
                InProgress = open,
            };
        }).ToList();

        return ApiResponse<PaginatedResponse<AttendanceHistoryItem>>.Ok(new PaginatedResponse<AttendanceHistoryItem>
        {
            Items = items,
            TotalCount = (int)total,
            Page = page,
            PageSize = pageSize,
        });
    }
}



