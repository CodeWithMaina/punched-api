using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PunchedApi.Application.Attendance.Verification;
using PunchedApi.Application.Authorization;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Attendance;

/// <summary>
/// The attendance hot path (plan §5.1, §9). Clock pipeline, exact order:
/// (1) idempotency replay → (2) ResolveActor → (3) effective policy →
/// (4) nothing-configured guard → (5) verification engine → (6) policy
/// isActive → (7) stale-open-session auto-close + session state check →
/// (8) transaction (event + session) → (9) audit → (10) store idempotency.
/// Direction comes from SERVER session state, never from the client: the
/// same physical QR drives both verbs (declarative clock-in / clock-out).
/// </summary>
public partial class AttendanceService : IAttendanceService
{
    /// <summary>V1 stale-session window (§9.2) — the tunable column arrives with Phase 4.</summary>
    private const int MaxOpenSessionHours = AttendancePolicyService.DefaultMaxOpenSessionHours;

    private readonly ApplicationDbContext _context;
    private readonly IAttendancePolicyService _policyService;
    private readonly IAttendanceVerificationEngine _verificationEngine;
    private readonly IIdempotencyService _idempotencyService;
    private readonly ILogger<AttendanceService> _logger;

    public AttendanceService(
        ApplicationDbContext context,
        IAttendancePolicyService policyService,
        IAttendanceVerificationEngine verificationEngine,
        IIdempotencyService idempotencyService,
        ILogger<AttendanceService> logger)
    {
        _context = context;
        _policyService = policyService;
        _verificationEngine = verificationEngine;
        _idempotencyService = idempotencyService;
        _logger = logger;
    }

    /// <summary>GET v1/attendance/status — read-only; no idempotency, no caching (§13.2).</summary>
    public async Task<ApiResponse<AttendanceStatusResponse>> StatusAsync(Guid actorUserId)
    {
        var actor = await ResolveActorAsync(actorUserId, AttendancePermissions.View);
        if (!actor.Success) return PropagateFailure<AttendanceStatusResponse>(actor);

        var policy = await _policyService.GetEffectivePolicyAsync(actor.Data!.BusinessId);
        if (!policy.IsActive)
            return ApiResponse<AttendanceStatusResponse>.Fail(
                "ATTENDANCE_DISABLED", "Attendance is currently paused for this business.");

        var open = await GetOpenSessionAsync(actor.Data.Actor.Id);
        var lastEvent = await _context.AttendanceEvents.AsNoTracking()
            .Where(e => e.BusinessId == actor.Data.BusinessId && e.StaffUserId == actor.Data.Actor.Id)
            .OrderByDescending(e => e.OccurredAt)
            .Select(e => new { e.EventType, e.OccurredAt })
            .FirstOrDefaultAsync();

        var response = await BuildStatusAsync(
            actor.Data.Actor.Id, actor.Data.BusinessId, open, lastEvent?.EventType, lastEvent?.OccurredAt);
        return ApiResponse<AttendanceStatusResponse>.Ok(response);
    }

    public Task<ApiResponse<AttendanceStatusResponse>> ClockInAsync(
        Guid actorUserId, AttendanceClockRequest request, string? idempotencyKey = null) =>
        ClockAsync(actorUserId, request, AttendanceEventType.ClockIn, idempotencyKey);

    public Task<ApiResponse<AttendanceStatusResponse>> ClockOutAsync(
        Guid actorUserId, AttendanceClockRequest request, string? idempotencyKey = null) =>
        ClockAsync(actorUserId, request, AttendanceEventType.ClockOut, idempotencyKey);

    /// <summary>GET v1/attendance/history (plan §13.2).</summary>
    public async Task<ApiResponse<PaginatedResponse<AttendanceHistoryItem>>> HistoryAsync(
        Guid actorUserId, AttendanceHistoryQuery query)
    {
        var actor = await ResolveActorAsync(actorUserId, AttendancePermissions.View);
        if (!actor.Success) return PropagateFailure<PaginatedResponse<AttendanceHistoryItem>>(actor);

        query ??= new AttendanceHistoryQuery();
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);

        if (query.From.HasValue && query.To.HasValue && query.From.Value > query.To.Value)
            return ApiResponse<PaginatedResponse<AttendanceHistoryItem>>.Fail(
                "INVALID_DATE_RANGE", "from must not be after to.");

        AttendanceEventType? eventTypeFilter = ParseEventType(query.EventType);
        if (query.EventType != null && !eventTypeFilter.HasValue)
            return ApiResponse<PaginatedResponse<AttendanceHistoryItem>>.Fail(
                "INVALID_EVENT_TYPE", "eventType must be CLOCK_IN or CLOCK_OUT.");

        var businessId = actor.Data!.BusinessId;
        var staffUserId = actor.Data.Actor.Id; // ALWAYS the actor — never a client-supplied staff id.

        var baseQuery = _context.AttendanceEvents.AsNoTracking()
            .Where(e => e.BusinessId == businessId && e.StaffUserId == staffUserId);

        if (query.LocationId.HasValue)
            baseQuery = baseQuery.Where(e => e.AttendanceLocationId == query.LocationId.Value);
        if (query.From.HasValue)
            baseQuery = baseQuery.Where(e => e.OccurredAt >= query.From.Value.ToDateTime(TimeOnly.MinValue));
        if (query.To.HasValue)
            baseQuery = baseQuery.Where(e => e.OccurredAt < query.To.Value.AddDays(1).ToDateTime(TimeOnly.MinValue));
        if (eventTypeFilter.HasValue)
            baseQuery = baseQuery.Where(e => e.EventType == eventTypeFilter.Value);

        var total = await baseQuery.LongCountAsync();

        var events = await baseQuery
            .OrderByDescending(e => e.OccurredAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var sessionIds = events.Where(e => e.AttendanceSessionId.HasValue)
            .Select(e => e.AttendanceSessionId!.Value).Distinct().ToList();
        var sessions = sessionIds.Count == 0
            ? new Dictionary<Guid, AttendanceSession>()
            : await _context.AttendanceSessions.AsNoTracking()
                .Where(s => sessionIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id);

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

    private async Task<ApiResponse<AttendanceStatusResponse>> ClockAsync(
        Guid actorUserId,
        AttendanceClockRequest request,
        AttendanceEventType eventType,
        string? idempotencyKey)
    {
        var now = DateTime.UtcNow;
        var requestHash = HashRequest(eventType, request);

        // (1) Idempotency replay: same key + body → stored response;
        //     same key + different body → 409 IDEMPOTENCY_CONFLICT (§11).
        if (!string.IsNullOrEmpty(idempotencyKey))
        {
            var (replayed, conflict) = await TryReplayAsync(idempotencyKey, actorUserId, requestHash);
            if (conflict)
                return ApiResponse<AttendanceStatusResponse>.Fail(
                    "IDEMPOTENCY_CONFLICT", "Idempotency key already used with a different request body.");
            if (replayed != null)
                return replayed;
        }

        // (2) Resolve the actor + scoped business. Staff/business identity is
        //     derived server-side ONLY — the DTO cannot carry either (§10.2).
        var actor = await ResolveActorAsync(actorUserId, AttendancePermissions.Clock);
        if (!actor.Success) return PropagateFailure<AttendanceStatusResponse>(actor);
        var scope = actor.Data!;

        // (3) Effective policy (missing row ⇒ implicit Standard default, §6.1).
        var policy = await _policyService.GetEffectivePolicyAsync(scope.BusinessId);

        // (4) Nothing-configured guard: no live attendance point means the
        //     scan cannot be for this business — an actionable 409 rather
        //     than a confusing INVALID_QR (§9.6).
        if (!await IsConfiguredAsync(scope.BusinessId))
            return ApiResponse<AttendanceStatusResponse>.Fail(
                "ATTENDANCE_NOT_CONFIGURED", "Attendance is not set up yet. Ask your manager to set it up.");

        // (5) Verification engine (short-circuits on first failure, §7).
        var context = new AttendanceVerificationContext(
            scope.Actor.Id,
            scope.Actor.Role.ToString(),
            scope.BusinessId,
            scope.Actor.Role == UserRole.Staff ? scope.Actor.StaffBusinessId : scope.BusinessId,
            eventType,
            policy,
            request.Token,
            null,
            now);
        var verdict = await _verificationEngine.VerifyAsync(context);
        if (!verdict.Success)
            return ApiResponse<AttendanceStatusResponse>.Fail(
                verdict.Error!.Code, verdict.Error!.Message);

        // (6) The policy only PAUSES clocking (§4.4) — module entitlement
        //     is the on/off authority and is enforced by [RequireModule].
        if (!policy.IsActive)
            return ApiResponse<AttendanceStatusResponse>.Fail(
                "ATTENDANCE_DISABLED", "Attendance is currently paused for this business.");

        var location = verdict.Data!.Location!;
        var credential = verdict.Data.Credential!;

        // (7) Session state, with the stale rule (§9.2) applied first so an
        //     overnight-forgotten clock-out never blocks the next morning.
        var openSession = await GetOpenSessionAsync(scope.Actor.Id);
        if (openSession != null && openSession.OpenedAt < now.AddHours(-MaxOpenSessionHours))
        {
            await AutoCloseStaleSessionAsync(scope.Actor, openSession, now);
            openSession = null;
        }

        if (eventType == AttendanceEventType.ClockIn && openSession != null)
            return ApiResponse<AttendanceStatusResponse>.Fail(
                "ALREADY_CLOCKED_IN", "You are already clocked in.");

        if (eventType == AttendanceEventType.ClockOut && openSession == null)
            return ApiResponse<AttendanceStatusResponse>.Fail(
                "NOT_CLOCKED_IN", "Clock in before clocking out.");

        // (8) TRANSACTION: the CLOCK_OUT-without-session check constraint and
        //     the ix_attendance_sessions_open_per_staff partial unique index
        //     are the DB-level guarantees; the event shares the transaction so
        //     a losing race rolls back with NO orphan event (§9.3).
        AttendanceSession? session = null;
        await using var tx = await _context.Database.BeginTransactionAsync();
        try
        {
            var evt = new AttendanceEvent
            {
                Id = Guid.NewGuid(),
                BusinessId = scope.BusinessId,
                StaffUserId = scope.Actor.Id,
                EventType = eventType,
                OccurredAt = now,
                AttendanceLocationId = location.Id,
                AttendanceQrCredentialId = credential.Id,
                Source = AttendanceEventSource.Standard,
                VerificationSummaryJson = SerializeSummaries(verdict.Data.Summaries),
                ClientIdempotencyKey = idempotencyKey,
                CreatedByUserId = scope.Actor.Id,
                CreatedAt = now,
                RecordedAt = now,
            };
            await _context.AttendanceEvents.AddAsync(evt);

            if (eventType == AttendanceEventType.ClockIn)
            {
                // Flush the event insert first: event.attendance_session_id and
                // sessions.opening_event_id form a deliberate FK cycle, which a
                // single SaveChanges batch cannot order for two Added rows.
                await _context.SaveChangesAsync();

                session = new AttendanceSession
                {
                    Id = Guid.NewGuid(),
                    BusinessId = scope.BusinessId,
                    StaffUserId = scope.Actor.Id,
                    OpeningEventId = evt.Id,
                    OpenedAt = now,
                    OpeningLocationId = location.Id,
                    Status = AttendanceSessionStatus.Open,
                    CreatedAt = now,
                };
                await _context.AttendanceSessions.AddAsync(session);
                evt.AttendanceSessionId = session.Id; // back-fill
            }
            else
            {
                session = openSession!;
                session.ClosingEventId = evt.Id;
                session.ClosedAt = now;
                session.ClosingLocationId = location.Id; // cross-location close is legitimate (O6)
                session.WorkedMinutes = (int)Math.Floor((now - session.OpenedAt).TotalMinutes); // V1: no break deduction
                session.Status = AttendanceSessionStatus.Closed;
                evt.AttendanceSessionId = session.Id;
            }

            // Operator visibility: "last scanned 3 h ago" (§6.3).
            var trackedCredential = await _context.AttendanceQrCredentials
                .FirstOrDefaultAsync(c => c.Id == credential.Id);
            if (trackedCredential != null) trackedCredential.LastUsedAt = now;

            await _context.SaveChangesAsync();

            // (9) Audit: ids and verifier summary only — never the raw token (§19.3).
            await _context.ApiEventLogs.AddAsync(new ApiEventLog
            {
                Id = Guid.NewGuid(),
                TenantId = scope.BusinessId,
                UserId = scope.Actor.Id,
                Endpoint = eventType == AttendanceEventType.ClockIn
                    ? "POST /v1/attendance/clock-in"
                    : "POST /v1/attendance/clock-out",
                Method = "POST",
                StatusCode = 200,
                CreatedAt = now,
                DetailsJson = SerializeDetails(new
                {
                    action = eventType == AttendanceEventType.ClockIn ? "CLOCK_IN" : "CLOCK_OUT",
                    actor = scope.Actor.Id,
                    staffUserId = scope.Actor.Id,
                    locationId = location.Id,
                    credentialId = credential.Id,
                    eventType = AttendanceVerification.WireValue(eventType),
                    sessionId = session.Id,
                    verifiers = verdict.Data.Summaries,
                }),
            });
            await _context.SaveChangesAsync();

            await tx.CommitAsync();
        }
        catch (DbUpdateException ex)
        {
            await tx.RollbackAsync();
            if (IsUniqueViolation(ex))
            {
                // The losing racer: the partial unique index (clock-in) or the
                // close-consistency state (clock-out) rejected the write.
                return ApiResponse<AttendanceStatusResponse>.Fail(
                    eventType == AttendanceEventType.ClockIn
                        ? "ALREADY_CLOCKED_IN" : "NOT_CLOCKED_IN",
                    eventType == AttendanceEventType.ClockIn
                        ? "You are already clocked in." : "Clock in before clocking out.");
            }
            _logger.LogError(ex, "Attendance clock failed for staff {StaffUserId} business {BusinessId}",
                scope.Actor.Id, scope.BusinessId);
            throw;
        }

        // (10) Store the idempotency response (first response wins; storage
        //      failure never breaks the primary operation, §11).
        // A closed session reports "not_clocked_in" — only an OPEN session
        // makes the status payload clocked_in.
        var response = await BuildStatusAsync(
            scope.Actor.Id, scope.BusinessId,
            eventType == AttendanceEventType.ClockIn ? session : null,
            eventType, now);
        if (!string.IsNullOrEmpty(idempotencyKey))
            await StoreIdempotencyAsync(idempotencyKey, actorUserId, requestHash, response);

        return ApiResponse<AttendanceStatusResponse>.Ok(response);
    }

    // [GUARDS]
}