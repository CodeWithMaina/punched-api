using System.ComponentModel.DataAnnotations;

namespace PunchedApi.Domain.Entities;

/// <summary>
/// Append-only attendance ledger — the Attendance counterpart of
/// <c>stamps</c>. Immutable; a future audited adjustment flow may only add
/// rows, never rewrite them.
/// </summary>
public class AttendanceEvent : BaseEntity
{
    /// <summary>
    /// FK to the business this event belongs to.
    /// </summary>
    public Guid BusinessId { get; set; }

    /// <summary>
    /// FK to the staff user. Always the server-derived authenticated user
    /// id — never client-supplied.
    /// </summary>
    public Guid StaffUserId { get; set; }

    /// <summary>
    /// Event direction (ClockIn / ClockOut + reserved values).
    /// </summary>
    public AttendanceEventType EventType { get; set; }

    /// <summary>
    /// UTC timestamp of the business-meaningful occurrence.
    /// </summary>
    public DateTime OccurredAt { get; set; }

    /// <summary>
    /// FK to the location. Null for future manual adjustments.
    /// </summary>
    public Guid? AttendanceLocationId { get; set; }

    /// <summary>
    /// FK to the credential scanned. A shared location QR is legitimately
    /// reused, so this is a plain indexed FK (deliberately NOT unique —
    /// do not copy the <c>stamps.qr_token_id</c> unique trick).
    /// </summary>
    public Guid? AttendanceQrCredentialId { get; set; }

    /// <summary>
    /// FK to the owning session. Required for CLOCK_OUT (check constraint).
    /// </summary>
    public Guid? AttendanceSessionId { get; set; }

    /// <summary>
    /// How the event was recorded (V1: Standard; Manual reserved).
    /// </summary>
    public AttendanceEventSource Source { get; set; } = AttendanceEventSource.Standard;

    /// <summary>
    /// JSON string <c>{ method, actorUserId, credentialId, locationId,
    /// verifiers:[{ type, passed, checkedAt }] }</c> — no raw token, no PII.
    /// </summary>
    public string VerificationSummaryJson { get; set; } = string.Empty;

    /// <summary>
    /// Client-supplied idempotency key. Unique per
    /// (business, staff, key); NULLs are distinct in PostgreSQL so
    /// non-idempotent writes are unaffected.
    /// </summary>
    [MaxLength(200)]
    public string? ClientIdempotencyKey { get; set; }

    /// <summary>
    /// FK to the user who recorded this event (server-derived actor).
    /// </summary>
    public Guid CreatedByUserId { get; set; }

    /// <summary>
    /// UTC server insert time. Diverges from <c>OccurredAt</c> only in a
    /// future offline flow.
    /// </summary>
    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;
}
