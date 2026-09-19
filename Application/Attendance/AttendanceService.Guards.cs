using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.Attendance.Verification;
using PunchedApi.Application.Authorization;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Attendance;

/// <summary>
/// Actor/scope guards and session helpers for <see cref="AttendanceService"/>
/// (the <c>LoyaltyStampingService.Guards.cs</c> partial-file precedent).
/// Every identity field here is derived server-side — §10.2.
/// </summary>
public partial class AttendanceService
{
    /// <summary>Permission codes used on the staff hot path (ModuleCatalog-backed).</summary>
    private static class AttendancePermissions
    {
        public const string View = "attendance.view";
        public const string Clock = "attendance.clock";
    }

    private sealed record ActorScope(User Actor, Guid BusinessId);

    /// <summary>
    /// Resolves the actor + scoped business id server-side from the JWT-derived
    /// user id (the <c>StampService.ResolveActorAsync</c> pattern):
    /// staff → <c>Users.StaffBusinessId</c>; owner → owned business;
    /// anyone else → FORBIDDEN. A missing (soft-deleted) user maps to
    /// FORBIDDEN with one clear message — §9.6.
    /// </summary>
    private async Task<ApiResponse<ActorScope>> ResolveActorAsync(Guid userId, string permissionCode)
    {
        // The User global query filter hides soft-deleted rows, so a deleted
        // staff member resolves to null here and answers FORBIDDEN.
        var actor = await _context.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId);

        if (actor == null)
            return ApiResponse<ActorScope>.Fail("FORBIDDEN", "Your account is not active.");

        if (actor.Role is not (UserRole.Staff or UserRole.Business))
            return ApiResponse<ActorScope>.Fail(
                "FORBIDDEN", "Only business owners and staff can use attendance.");

        if (!PermissionMatrix.HasPermission(actor.Role.ToString(), permissionCode))
            return ApiResponse<ActorScope>.Fail(
                "FORBIDDEN", "You do not have permission for this attendance action.");

        if (actor.Role == UserRole.Staff)
        {
            if (actor.StaffBusinessId == null)
                return ApiResponse<ActorScope>.Fail(
                    "NOT_LINKED", "Staff user is not linked to a business.");
            return ApiResponse<ActorScope>.Ok(new ActorScope(actor, actor.StaffBusinessId.Value));
        }

        var ownedBusinessId = await _context.Businesses.AsNoTracking()
            .Where(b => b.OwnerId == actor.Id && !b.IsDeleted)
            .Select(b => (Guid?)b.Id)
            .FirstOrDefaultAsync();

        if (ownedBusinessId == null)
            return ApiResponse<ActorScope>.Fail("NOT_FOUND", "No business found for this account.");

        return ApiResponse<ActorScope>.Ok(new ActorScope(actor, ownedBusinessId.Value));
    }

    private static ApiResponse<T> PropagateFailure<T>(ApiResponse<ActorScope> scope) =>
        ApiResponse<T>.Fail(scope.Error!.Code, scope.Error!.Message);

    /// <summary>
    /// Does the business have anything to scan at all — an active attendance
    /// point with a live credential? If not, the scan cannot be for this
    /// business and the actionable ATTENDANCE_NOT_CONFIGURED is surfaced
    /// before the engine runs (§9.6).
    /// </summary>
    /// <summary>
    /// Has the business ever configured attendance at all — any attendance
    /// point plus a live credential? A location that was created and later
    /// deactivated still counts as "configured" (the engine then answers
    /// LOCATION_INACTIVE, which is actionable); a business with no locations
    /// or no live credential at all has nothing to scan (§9.6).
    /// </summary>
    private async Task<bool> IsConfiguredAsync(Guid businessId)
    {
        var hasAnyLocation = await _context.AttendanceLocations.AsNoTracking()
            .AnyAsync(l => l.BusinessId == businessId);
        if (!hasAnyLocation) return false;

        return await _context.AttendanceQrCredentials.AsNoTracking()
            .AnyAsync(c => c.BusinessId == businessId && c.Status == AttendanceCredentialStatus.Active);
    }

    private Task<AttendanceSession?> GetOpenSessionAsync(Guid staffUserId) =>
        _context.AttendanceSessions.FirstOrDefaultAsync(s => s.StaffUserId == staffUserId && s.ClosedAt == null);

    /// <summary>
    /// The stale-session rule (§9.2 / decision D7): an OPEN session older than
    /// <c>MaxOpenSessionHours</c> (default 16 h) is auto-closed with a capped
    /// duration and an audit entry, so an overnight-forgotten clock-out never
    /// blocks the next morning. NO fabricated CLOCK_OUT event is written
    /// (nobody performed that action); the close-consistency check constraint
    /// demands a non-null <c>closing_event_id</c>, so the close is anchored to
    /// the session's opening event.
    /// </summary>
    private async Task AutoCloseStaleSessionAsync(User actor, AttendanceSession stale, DateTime now)
    {
        var closedAt = stale.OpenedAt.AddHours(MaxOpenSessionHours);
        stale.ClosedAt = closedAt;
        stale.ClosingEventId = stale.OpeningEventId; // anchored — no CLOCK_OUT ledger event fabricated
        stale.ClosingLocationId = stale.OpeningLocationId;
        stale.WorkedMinutes = MaxOpenSessionHours * 60; // capped duration
        stale.Status = AttendanceSessionStatus.Closed;
        await _context.SaveChangesAsync();

        await _context.ApiEventLogs.AddAsync(new ApiEventLog
        {
            Id = Guid.NewGuid(),
            TenantId = stale.BusinessId,
            UserId = actor.Id,
            Endpoint = "POST /v1/attendance/clock-in",
            Method = "POST",
            StatusCode = 200,
            CreatedAt = now,
            DetailsJson = SerializeDetails(new
            {
                action = "SESSION_AUTO_CLOSED",
                actor = actor.Id,
                staffUserId = stale.StaffUserId,
                sessionId = stale.Id,
                openedAt = stale.OpenedAt,
                closedAt,
                workedMinutes = stale.WorkedMinutes,
                maxOpenSessionHours = MaxOpenSessionHours,
            }),
        });
        await _context.SaveChangesAsync();

        _logger.LogWarning(
            "Attendance session {SessionId} auto-closed (stale > {Hours}h) for staff {StaffUserId}",
            stale.Id, MaxOpenSessionHours, stale.StaffUserId);
    }

    /// <summary>Reads a completed idempotency entry; storage failure never breaks the primary op (§11).</summary>
    private async Task<(ApiResponse<AttendanceStatusResponse>? Replayed, bool Conflict)> TryReplayAsync(
        string key, Guid userId, string requestHash)
    {
        try
        {
            var lookup = await _idempotencyService.TryGetAsync(key, userId, requestHash);
            if (!lookup.Found) return (null, false);
            if (lookup.Conflict) return (null, true);
            var replay = JsonSerializer.Deserialize<ApiResponse<AttendanceStatusResponse>>(lookup.ResponseJson ?? "");
            return (replay, false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Attendance idempotency lookup failed for key {Key} — continuing", key);
            return (null, false);
        }
    }

    private async Task StoreIdempotencyAsync(
        string key, Guid userId, string requestHash, AttendanceStatusResponse response)
    {
        try
        {
            // The full ApiResponse envelope is stored (the StampService
            // pattern) so the replay is indistinguishable from the original.
            await _idempotencyService.StoreAsync(
                key, userId, requestHash,
                JsonSerializer.Serialize(ApiResponse<AttendanceStatusResponse>.Ok(response)));
        }
        catch (Exception ex)
        {
            // Idempotency storage must never break the primary operation.
            _logger.LogWarning(ex, "Failed to store attendance idempotency entry for key {Key}", key);
        }
    }

    private static string HashRequest(AttendanceEventType eventType, AttendanceClockRequest request)
    {
        // The verb is part of the hash: the same key + body must never replay
        // a clock-in response for a clock-out request.
        var json = JsonSerializer.Serialize(new { eventType, token = request.Token },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }

    private static string SerializeDetails(object details) =>
        JsonSerializer.Serialize(details, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
        });

    /// <summary>Merged verifier summaries: <c>[{...},{...}]</c> — no raw token, no PII.</summary>
    private static string SerializeSummaries(IReadOnlyList<string> summaries) =>
        JsonSerializer.Serialize(summaries);

    private static AttendanceEventType? ParseEventType(string? wire)
    {
        if (string.IsNullOrWhiteSpace(wire)) return null;
        foreach (AttendanceEventType type in Enum.GetValues<AttendanceEventType>())
        {
            if (string.Equals(AttendanceVerification.WireValue(type), wire, StringComparison.OrdinalIgnoreCase))
                return type;
        }
        return null;
    }

    /// <summary>
    /// Builds the status payload (plan §13.2): open-session state + today's
    /// worked minutes across all of the staff member's sessions.
    /// </summary>
    private async Task<AttendanceStatusResponse> BuildStatusAsync(
        Guid staffUserId,
        Guid businessId,
        AttendanceSession? openSession,
        AttendanceEventType? lastEventType,
        DateTime? lastEventAt)
    {
        var now = DateTime.UtcNow;
        var todayStart = now.Date;
        var todayEnd = todayStart.AddDays(1);

        // Today's minutes: overlap of every session (open or closed) with today (UTC).
        var recent = await _context.AttendanceSessions.AsNoTracking()
            .Where(s => s.StaffUserId == staffUserId && s.OpenedAt < todayEnd &&
                        (s.ClosedAt == null || s.ClosedAt > todayStart))
            .Select(s => new { s.OpenedAt, s.ClosedAt })
            .ToListAsync();

        var todayWorkedMinutes = (int)recent.Sum(s =>
            (Math.Min((s.ClosedAt ?? now).Ticks, todayEnd.Ticks)
             - Math.Max(s.OpenedAt.Ticks, todayStart.Ticks)) / TimeSpan.TicksPerMinute);

        string? locationName = null;
        if (openSession?.OpeningLocationId != null)
        {
            locationName = await _context.AttendanceLocations.AsNoTracking()
                .Where(l => l.Id == openSession.OpeningLocationId.Value)
                .Select(l => l.Name)
                .FirstOrDefaultAsync();
        }

        return new AttendanceStatusResponse
        {
            State = openSession == null ? "not_clocked_in" : "clocked_in",
            BusinessId = businessId,
            StaffUserId = staffUserId,
            LocationId = openSession?.OpeningLocationId,
            LocationName = locationName,
            OpenedAt = openSession?.OpenedAt,
            ElapsedMinutes = openSession == null
                ? null : (int)Math.Floor((now - openSession.OpenedAt).TotalMinutes),
            TodayWorkedMinutes = todayWorkedMinutes,
            LastEventType = lastEventType.HasValue ? AttendanceVerification.WireValue(lastEventType.Value) : null,
            LastEventAt = lastEventAt,
        };
    }

    /// <summary>
    /// Unique-violation detection for the losing-race path. PostgreSQL raises
    /// SqlState 23505 on <c>ix_attendance_sessions_open_per_staff</c>; SQLite
    /// raises SqliteException with a UNIQUE constraint message.
    /// </summary>
    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        var inner = ex.InnerException;
        if (inner == null) return false;
        if (inner.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)) return true;
        if (inner.GetType().Name.Contains("PostgresException"))
            return inner.Message.Contains("23505", StringComparison.OrdinalIgnoreCase);
        return false;
    }
}