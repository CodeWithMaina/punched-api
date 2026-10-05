using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.Attendance.Verification;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Attendance;

/// <summary>
/// Owner-initiated MANUAL clocking (plan §13.3 extension). Where the staff
/// hot path proves presence with a scanned QR, here the authenticated business
/// owner IS the trust anchor: they record a shift on behalf of a specific
/// staff member. Every event written here is
/// <see cref="AttendanceEventSource.Manual"/> and carries the owner's user id
/// in <c>CreatedByUserId</c>, so manual and automatic entries remain
/// distinguishable in the ledger.
///
/// <para>Guarantees mirrored from the self-serve path: duplicate active
/// sessions are impossible (the partial unique index is the backstop), an
/// invalid clock-out is refused, and the staff id is always business-scoped so
/// a foreign id answers <c>STAFF_NOT_FOUND</c> (never an enumeration leak).</para>
/// </summary>
public partial class AttendanceLocationService
{
    public Task<ApiResponse<OwnerAttendanceClockResponse>> OwnerClockInAsync(
        Guid ownerUserId, Guid staffUserId, OwnerAttendanceClockRequest request) =>
        OwnerClockAsync(ownerUserId, staffUserId, request, AttendanceEventType.ClockIn);

    public Task<ApiResponse<OwnerAttendanceClockResponse>> OwnerClockOutAsync(
        Guid ownerUserId, Guid staffUserId, OwnerAttendanceClockRequest request) =>
        OwnerClockAsync(ownerUserId, staffUserId, request, AttendanceEventType.ClockOut);

    private async Task<ApiResponse<OwnerAttendanceClockResponse>> OwnerClockAsync(
        Guid ownerUserId,
        Guid staffUserId,
        OwnerAttendanceClockRequest request,
        AttendanceEventType eventType)
    {
        request ??= new OwnerAttendanceClockRequest();

        var businessId = await ResolveBusinessIdAsync(ownerUserId);
        if (businessId == null)
            return ApiResponse<OwnerAttendanceClockResponse>.Fail("NOT_FOUND", "Business not found.");

        // Staff must belong to THIS business → a foreign id is NOT_FOUND-shaped.
        var staff = await _context.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == staffUserId
                                      && u.StaffBusinessId == businessId
                                      && !u.IsDeleted);
        if (staff == null)
            return ApiResponse<OwnerAttendanceClockResponse>.Fail(
                "STAFF_NOT_FOUND", "Staff member not found.");

        // The pause switch applies to manual entries too — pausing means
        // "nobody is clocking right now", however the event is recorded.
        if (!await IsPolicyActiveAsync(businessId.Value))
            return ApiResponse<OwnerAttendanceClockResponse>.Fail(
                "ATTENDANCE_DISABLED", "Attendance is currently paused for this business.");

        // Optional location, resolved and validated against the same business.
        AttendanceLocation? location = null;
        if (request.LocationId.HasValue)
        {
            location = await _context.AttendanceLocations
                .FirstOrDefaultAsync(l => l.Id == request.LocationId.Value
                                          && l.BusinessId == businessId);
            if (location == null)
                return ApiResponse<OwnerAttendanceClockResponse>.Fail("NOT_FOUND", "Location not found.");
            if (!location.IsActive)
                return ApiResponse<OwnerAttendanceClockResponse>.Fail(
                    "LOCATION_INACTIVE", "This attendance point is currently disabled.");
        }

        var now = DateTime.UtcNow;
        var summary = SerializeManualSummary(ownerUserId, request.Note);

        var openSession = await _context.AttendanceSessions
            .FirstOrDefaultAsync(s => s.StaffUserId == staffUserId && s.ClosedAt == null);

        if (eventType == AttendanceEventType.ClockIn && openSession != null)
            return ApiResponse<OwnerAttendanceClockResponse>.Fail(
                "ALREADY_CLOCKED_IN", $"{staff.FullName} is already clocked in.");

        if (eventType == AttendanceEventType.ClockOut && openSession == null)
            return ApiResponse<OwnerAttendanceClockResponse>.Fail(
                "NOT_CLOCKED_IN", $"{staff.FullName} is not clocked in.");

        var endpoint = eventType == AttendanceEventType.ClockIn
            ? "POST /v1/businesses/me/attendance/staff/{staffUserId}/clock-in"
            : "POST /v1/businesses/me/attendance/staff/{staffUserId}/clock-out";

        try
        {
            await using var tx = await _context.Database.BeginTransactionAsync();

            var evt = new AttendanceEvent
            {
                Id = Guid.NewGuid(),
                BusinessId = businessId.Value,
                StaffUserId = staffUserId,
                EventType = eventType,
                OccurredAt = now,
                AttendanceLocationId = location?.Id,
                AttendanceQrCredentialId = null,   // manual: no credential involved
                Source = AttendanceEventSource.Manual,
                VerificationSummaryJson = summary,
                CreatedByUserId = ownerUserId,     // the owner who recorded it
                CreatedAt = now,
                RecordedAt = now,
            };
            await _context.AttendanceEvents.AddAsync(evt);

            AttendanceSession session;
            if (eventType == AttendanceEventType.ClockIn)
            {
                // Flush the event insert first: event.attendance_session_id and
                // sessions.opening_event_id are a deliberate FK cycle that a
                // single batch cannot order for two Added rows.
                await _context.SaveChangesAsync();

                session = new AttendanceSession
                {
                    Id = Guid.NewGuid(),
                    BusinessId = businessId.Value,
                    StaffUserId = staffUserId,
                    OpeningEventId = evt.Id,
                    OpenedAt = now,
                    OpeningLocationId = location?.Id,
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
                session.ClosingLocationId = location?.Id ?? session.OpeningLocationId;
                session.ClosedAt = now;
                session.WorkedMinutes = (int)Math.Floor((now - session.OpenedAt).TotalMinutes);
                session.Status = AttendanceSessionStatus.Closed;
                evt.AttendanceSessionId = session.Id;
            }

            await _context.SaveChangesAsync();

            await AuditAsync(
                eventType == AttendanceEventType.ClockIn
                    ? AttendanceAuditActions.ManualClockIn
                    : AttendanceAuditActions.ManualClockOut,
                businessId.Value,
                ownerUserId,
                endpoint,
                locationId: location?.Id,
                details: new
                {
                    staffUserId,
                    eventType = AttendanceVerification.WireValue(eventType),
                    sessionId = session.Id,
                    note = NormalizeOptional(request.Note),
                });

            await tx.CommitAsync();

            var resolvedLocationId = eventType == AttendanceEventType.ClockIn
                ? location?.Id
                : session.ClosingLocationId;
            var resolvedLocationName = await ResolveLocationNameAsync(
                location, businessId.Value, resolvedLocationId);

            return ApiResponse<OwnerAttendanceClockResponse>.Ok(new OwnerAttendanceClockResponse
            {
                StaffUserId = staffUserId,
                State = eventType == AttendanceEventType.ClockIn ? "clocked_in" : "not_clocked_in",
                EventType = AttendanceVerification.WireValue(eventType),
                OccurredAt = now,
                LocationId = resolvedLocationId,
                LocationName = resolvedLocationName,
                OpenedAt = session.OpenedAt,
                WorkedMinutes = eventType == AttendanceEventType.ClockOut ? session.WorkedMinutes : null,
            });
        }
        catch (DbUpdateException ex) when (IsDuplicateSessionViolation(ex))
        {
            // Lost the race to the partial unique open-per-staff index (or the
            // close-consistency check). Report the same actionable conflict the
            // optimistic check above would have.
            _logger.LogInformation(ex,
                "Manual attendance race for staff {StaffUserId} business {BusinessId}",
                staffUserId, businessId.Value);

            return ApiResponse<OwnerAttendanceClockResponse>.Fail(
                eventType == AttendanceEventType.ClockIn ? "ALREADY_CLOCKED_IN" : "NOT_CLOCKED_IN",
                eventType == AttendanceEventType.ClockIn
                    ? $"{staff.FullName} is already clocked in."
                    : $"{staff.FullName} is not clocked in.");
        }
    }

    /// <summary>Missing policy row ⇒ implicit Standard default (active).</summary>
    private async Task<bool> IsPolicyActiveAsync(Guid businessId)
    {
        var isActive = await _context.AttendancePolicies.AsNoTracking()
            .Where(p => p.BusinessId == businessId)
            .Select(p => (bool?)p.IsActive)
            .FirstOrDefaultAsync();
        return isActive ?? true;
    }

    /// <summary>JSON audit summary for a manual event — { method, recordedBy, actorUserId, note }.</summary>
    private static string SerializeManualSummary(Guid actorUserId, string? note) =>
        JsonSerializer.Serialize(new
        {
            method = AttendanceVerification.WireValue(AttendanceVerificationMethod.AuthenticatedUser),
            recordedBy = "MANUAL",
            actorUserId,
            note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
        });

    private async Task<string?> ResolveLocationNameAsync(
        AttendanceLocation? provided, Guid businessId, Guid? locationId)
    {
        if (locationId == null) return null;
        if (provided != null && provided.Id == locationId) return provided.Name;

        return await _context.AttendanceLocations.AsNoTracking()
            .Where(l => l.Id == locationId && l.BusinessId == businessId)
            .Select(l => l.Name)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// Unique-violation detection for the losing-race path (the
    /// <c>AttendanceService.IsUniqueViolation</c> shape): PostgreSQL raises
    /// SqlState 23505; SQLite raises a UNIQUE-constraint message.
    /// </summary>
    private static bool IsDuplicateSessionViolation(DbUpdateException ex)
    {
        var inner = ex.InnerException;
        if (inner == null) return false;
        if (inner.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)) return true;
        if (inner.GetType().Name.Contains("PostgresException"))
            return inner.Message.Contains("23505", StringComparison.OrdinalIgnoreCase);
        return false;
    }

    // [OWNER_CLOCK]
}

