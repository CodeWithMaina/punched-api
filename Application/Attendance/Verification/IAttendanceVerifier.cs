using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Application.Attendance.Verification;

/// <summary>
/// Per-request verification input (plan §7.2). Every identity field is derived
/// server-side from the authenticated principal — nothing here is ever read
/// from a client-supplied staff or business identifier (§10.2).
/// </summary>
/// <param name="ActorUserId">From the JWT <c>userId</c> claim (structurally enforces "staff never enters identity").</param>
/// <param name="ActorRole">Role claim string (Business / Staff).</param>
/// <param name="BusinessId">Server-resolved scope (owner resolver / StaffBusinessId) — the tenant for every check.</param>
/// <param name="StaffBusinessId">The actor's linked business; null ⇒ NOT_LINKED for staff.</param>
/// <param name="EventType">The declarative verb being verified (ClockIn / ClockOut).</param>
/// <param name="Policy">The business's effective attendance policy (drives which verifiers run).</param>
/// <param name="CredentialToken">Raw QR payload — present only for token-based methods.</param>
/// <param name="ClientFingerprint">Reserved for future trusted-device / biometric methods.</param>
/// <param name="NowUtc">UTC timestamp of the check.</param>
public sealed record AttendanceVerificationContext(
    Guid ActorUserId,
    string ActorRole,
    Guid BusinessId,
    Guid? StaffBusinessId,
    AttendanceEventType EventType,
    AttendancePolicy Policy,
    string? CredentialToken,
    string? ClientFingerprint,
    DateTime NowUtc);

/// <summary>
/// Result of a single verifier run. <see cref="SummaryJson"/> is a fragment
/// like <c>{"type":"QR","passed":true,"checkedAt":"…"}</c> that the engine
/// merges and Phase 3 persists on the attendance event — never the raw token,
/// never PII.
/// </summary>
public sealed record AttendanceVerificationOutcome(
    bool Passed,
    string? ErrorCode,
    string? Message,
    AttendanceQrCredential? Credential,
    AttendanceLocation? Location,
    string SummaryJson)
{
    public static AttendanceVerificationOutcome Pass(string summaryJson) =>
        new(true, null, null, null, null, summaryJson);

    public static AttendanceVerificationOutcome Pass(
        string summaryJson, AttendanceQrCredential credential, AttendanceLocation location) =>
        new(true, null, null, credential, location, summaryJson);

    public static AttendanceVerificationOutcome Fail(string code, string message, string summaryJson) =>
        new(false, code, message, null, null, summaryJson);
}

/// <summary>
/// A single policy-driven verification step. Verifiers are stateless with
/// respect to attendance: they must NEVER write attendance state and must
/// NEVER log or return the raw QR token (plan §7.4).
/// </summary>
public interface IAttendanceVerifier
{
    /// <summary>The method this verifier implements (matched against the policy's required set).</summary>
    AttendanceVerificationMethod Method { get; }

    Task<AttendanceVerificationOutcome> VerifyAsync(AttendanceVerificationContext context);
}