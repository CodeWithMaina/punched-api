using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using PunchedApi.Application.Attendance;
using PunchedApi.Application.Attendance.Verification;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Application.DTOs;

/// <summary>
/// Attendance location + policy DTOs (plan §13.3–§13.4). Clock-in/out request
/// and status/history DTOs are added by Phase 3.
/// The raw QR token appears in exactly ONE DTO
/// (<see cref="AttendanceQrCredentialResponse"/>) and only as the return value
/// of mint/rotate — it is never retrievable again (§8.6).
/// </summary>
public sealed class AttendanceLocationSummaryResponse
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; }

    /// <summary>Is there a live QR for this location? The token itself is never returned (§13.3).</summary>
    [JsonPropertyName("hasActiveCredential")]
    public bool HasActiveCredential { get; set; }

    /// <summary>Last accepted scan of this location's credentials — operator visibility only.</summary>
    [JsonPropertyName("lastUsedAt")]
    public DateTime? LastUsedAt { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }
}

public sealed class AttendanceLocationDetailResponse
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("businessId")]
    public Guid BusinessId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; }

    [JsonPropertyName("hasActiveCredential")]
    public bool HasActiveCredential { get; set; }

    [JsonPropertyName("lastUsedAt")]
    public DateTime? LastUsedAt { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }
}

public sealed class CreateAttendanceLocationRequest
{
    [JsonPropertyName("name")]
    [Required, MaxLength(120)]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    [MaxLength(300)]
    public string? Description { get; set; }
}

public sealed class UpdateAttendanceLocationRequest
{
    [JsonPropertyName("name")]
    [MaxLength(120)]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    [MaxLength(300)]
    public string? Description { get; set; }

    [JsonPropertyName("isActive")]
    public bool? IsActive { get; set; }
}

/// <summary>
/// Result of a QR mint/rotate. <see cref="Token"/> is the raw printable payload
/// (<c>punched:attendance:v1:&lt;43-char token&gt;</c>) and is returned ONCE —
/// only its SHA-256 hex is stored (plan §8.3).
/// </summary>
public sealed class AttendanceQrCredentialResponse
{
    [JsonPropertyName("credentialId")]
    public Guid CredentialId { get; set; }

    [JsonPropertyName("locationId")]
    public Guid LocationId { get; set; }

    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    /// <summary>SCREAMING_SNAKE credential lifecycle value (<c>ACTIVE</c>).</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Effective attendance policy. Values are wire-shaped SCREAMING_SNAKE strings
/// so the API contract does not shift if C# member names are refactored (§6.6).
/// </summary>
public sealed class AttendancePolicyResponse
{
    /// <summary><c>STANDARD</c> in V1 (the enum stays open for future modes).</summary>
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = string.Empty;

    [JsonPropertyName("requiredVerifications")]
    public string[] RequiredVerifications { get; set; } = Array.Empty<string>();

    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; }

    /// <summary>
    /// V1 default (16 h). The stale-open-session rule of plan §9.2 is capped by
    /// this; the persisted column arrives with the Phase 4 settings write.
    /// </summary>
    [JsonPropertyName("maxOpenSessionHours")]
    public int MaxOpenSessionHours { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>
/// Owner-facing attendance settings card (plan §15.2). Combines the policy
/// (on/off + method set + stale-session window) with derived readiness state
/// so a single response can render the "configured?" / "paused" UX. The
/// module entitlement toggle (plan enable/disable) is rendered separately by
/// the existing modules page — see §4.4; this is the runtime pause + method
/// configuration surface.
/// </summary>
public sealed class AttendanceSettingsResponse
{
    /// <summary>Pause clock-ins without deleting credentials/rows.</summary>
    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; }

    /// <summary><c>STANDARD</c> in V1 (enum stays open for future modes).</summary>
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = string.Empty;

    [JsonPropertyName("requiredVerifications")]
    public string[] RequiredVerifications { get; set; } = Array.Empty<string>();

    /// <summary>Stale-open-session cap (hours), clamped to 4..48 in V1.</summary>
    [JsonPropertyName("maxOpenSessionHours")]
    public int MaxOpenSessionHours { get; set; }

    /// <summary>True once at least one location exists (plan §4.4 readiness).</summary>
    [JsonPropertyName("isConfigured")]
    public bool IsConfigured { get; set; }

    [JsonPropertyName("locationCount")]
    public int LocationCount { get; set; }

    /// <summary>All-live-QR count across every location.</summary>
    [JsonPropertyName("activeCredentialCount")]
    public int ActiveCredentialCount { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>
/// PUT v1/businesses/me/attendance/settings request body (whole-policy
/// replacement, plan §15.2). Validated by AttendanceValidators + the policy
/// service's static helpers (INVALID_MODE / INVALID_VERIFICATION_SET /
/// INVALID_SESSION_WINDOW).
/// </summary>
public sealed class AttendanceSettingsRequest
{
    /// <summary>V1 only allows "STANDARD" (case-insensitive).</summary>
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "STANDARD";

    /// <summary>
    /// Non-empty set of verifier methods the clock flow must run. Must include
    /// "AUTHENTICATED_USER"; a policy demanding an unregistered method
    /// fails closed at scan time (VERIFICATION_METHOD_UNAVAILABLE).
    /// </summary>
    [JsonPropertyName("requiredVerifications")]
    public string[] RequiredVerifications { get; set; } =
        [AttendanceVerification.WireValue(AttendanceVerificationMethod.AuthenticatedUser),
         AttendanceVerification.WireValue(AttendanceVerificationMethod.Qr)];

    /// <summary>Pause clock-ins without deleting credentials/rows.</summary>
    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; } = true;

    /// <summary>Stale-open-session cap (hours), clamped to 4..48 in V1.</summary>
    [JsonPropertyName("maxOpenSessionHours")]
    public int MaxOpenSessionHours { get; set; } = AttendancePolicyService.DefaultMaxOpenSessionHours;
}

/// <summary>One staff row in the manager overview (plan §13.3 / §18.1).</summary>
public sealed class AttendanceOverviewStaffItem
{
    [JsonPropertyName("staffUserId")]
    public Guid StaffUserId { get; set; }

    [JsonPropertyName("fullName")]
    public string FullName { get; set; } = string.Empty;

    /// <summary>"NOT_CLOCKED_IN" | "CLOCKED_IN" | "CLOCKED_OUT" today.</summary>
    [JsonPropertyName("state")]
    public string State { get; set; } = "NOT_CLOCKED_IN";

    [JsonPropertyName("openedAt")]
    public DateTime? OpenedAt { get; set; }

    [JsonPropertyName("workedMinutes")]
    public int? WorkedMinutes { get; set; }

    [JsonPropertyName("locationName")]
    public string? LocationName { get; set; }
}

/// <summary>GET v1/businesses/me/attendance/overview response (plan §13.3).</summary>
public sealed class AttendanceOverviewResponse
{
    [JsonPropertyName("date")]
    public DateOnly Date { get; set; }

    [JsonPropertyName("clockedInCount")]
    public int ClockedInCount { get; set; }

    [JsonPropertyName("notClockedInCount")]
    public int NotClockedInCount { get; set; }

    [JsonPropertyName("totalWorkedMinutes")]
    public int TotalWorkedMinutes { get; set; }

    [JsonPropertyName("staff")]
    public List<AttendanceOverviewStaffItem> Staff { get; set; } = new();
}

// ═══════════════════════════════════════════════════════════════
//  PHASE 3 — STAFF HOT-PATH DTOs (plan §13.2, §18)
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// POST v1/attendance/clock-in and clock-out request body (plan §13.2).
/// THE ONLY FIELD: no staff or business identity can ever be supplied —
/// both are derived server-side from the JWT (§10.2).
/// </summary>
public sealed class AttendanceClockRequest
{
    /// <summary>Raw printed QR payload: punched:attendance:v1:&lt;token&gt;.</summary>
    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;
}

/// <summary>
/// Attendance state — returned by GET v1/attendance/status AND by both
/// clock mutations, so the client updates its whole view from one payload.
/// </summary>
public sealed class AttendanceStatusResponse
{
    /// <summary>"not_clocked_in" | "clocked_in".</summary>
    [JsonPropertyName("state")]
    public string State { get; set; } = "not_clocked_in";

    [JsonPropertyName("businessId")]
    public Guid BusinessId { get; set; }

    [JsonPropertyName("staffUserId")]
    public Guid StaffUserId { get; set; }

    [JsonPropertyName("locationId")]
    public Guid? LocationId { get; set; }

    [JsonPropertyName("locationName")]
    public string? LocationName { get; set; }

    [JsonPropertyName("openedAt")]
    public DateTime? OpenedAt { get; set; }

    [JsonPropertyName("elapsedMinutes")]
    public int? ElapsedMinutes { get; set; }

    /// <summary>Minutes worked today across all sessions (V1: no break deduction).</summary>
    [JsonPropertyName("todayWorkedMinutes")]
    public int TodayWorkedMinutes { get; set; }

    /// <summary>"CLOCK_IN" | "CLOCK_OUT" — the most recent ledger event.</summary>
    [JsonPropertyName("lastEventType")]
    public string? LastEventType { get; set; }

    [JsonPropertyName("lastEventAt")]
    public DateTime? LastEventAt { get; set; }
}

/// <summary>
/// One row of the staff member's own history (GET v1/attendance/history,
/// plan §13.2). Always scoped to the authenticated actor's business AND
/// staff user id — this surface never returns another member's rows.
/// </summary>
public sealed class AttendanceHistoryItem
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    /// <summary>SCREAMING_SNAKE wire value ("CLOCK_IN" / "CLOCK_OUT").</summary>
    [JsonPropertyName("eventType")]
    public string EventType { get; set; } = string.Empty;

    [JsonPropertyName("occurredAt")]
    public DateTime OccurredAt { get; set; }

    [JsonPropertyName("locationId")]
    public Guid? LocationId { get; set; }

    [JsonPropertyName("locationName")]
    public string? LocationName { get; set; }

    [JsonPropertyName("sessionId")]
    public Guid? SessionId { get; set; }

    /// <summary>Computed on close; null while the owning session is open.</summary>
    [JsonPropertyName("workedMinutes")]
    public int? WorkedMinutes { get; set; }

    /// <summary>True when the owning session is still open.</summary>
    [JsonPropertyName("inProgress")]
    public bool InProgress { get; set; }
}

/// <summary>GET v1/attendance/history query parameters (plan §13.2).</summary>
public sealed class AttendanceHistoryQuery
{
    [JsonPropertyName("from")]
    public DateOnly? From { get; set; }

    [JsonPropertyName("to")]
    public DateOnly? To { get; set; }

    [JsonPropertyName("locationId")]
    public Guid? LocationId { get; set; }

    /// <summary>Optional "CLOCK_IN" / "CLOCK_OUT" filter.</summary>
    [JsonPropertyName("eventType")]
    public string? EventType { get; set; }

    [JsonPropertyName("page")]
    public int Page { get; set; } = 1;

    [JsonPropertyName("pageSize")]
    public int PageSize { get; set; } = 25;
}