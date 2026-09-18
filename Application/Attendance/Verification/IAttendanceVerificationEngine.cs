using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Application.Attendance.Verification;

/// <summary>
/// The merged engine result: every verifier summary in deterministic order,
/// the resolved credential/location (when a token method passed), and — on
/// failure — the failing method plus the single first-failure code/message
/// (the engine never continues past a failure, plan §7.4).
/// </summary>
/// <param name="Method">The method that failed; null when the verdict passed (or when the policy itself was unrunnable).</param>
public sealed record AttendanceVerificationResult(
    AttendanceVerificationMethod? Method,
    bool Passed,
    IReadOnlyList<string> Summaries,
    AttendanceQrCredential? Credential,
    AttendanceLocation? Location,
    string? FailureCode,
    string? FailureMessage)
{
    public static AttendanceVerificationResult Success(
        IReadOnlyList<string> summaries, AttendanceQrCredential? credential, AttendanceLocation? location) =>
        new(null, true, summaries, credential, location, null, null);

    public static AttendanceVerificationResult Failure(
        AttendanceVerificationMethod? method, string code, string message, IReadOnlyList<string> summaries) =>
        new(method, false, summaries, null, null, code, message);
}

/// <summary>
/// Orchestrates the policy's required verifiers in deterministic order
/// (AuthenticatedUser first, then Qr, then future methods), short-circuits on
/// the first failure, and fails closed when a policy demands a method no
/// registered verifier implements (<c>VERIFICATION_METHOD_UNAVAILABLE</c>) —
/// the same fail-closed philosophy as <c>RequireModuleAttribute</c>; nothing
/// is ever silently skipped.
/// </summary>
public interface IAttendanceVerificationEngine
{
    Task<ApiResponse<AttendanceVerificationResult>> VerifyAsync(AttendanceVerificationContext context);
}