using Microsoft.Extensions.Logging;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Application.Attendance.Verification;

/// <summary>
/// Policy-driven verifier orchestration (plan §7.2). Resolves the policy's
/// required methods against the registered <see cref="IAttendanceVerifier"/>s,
/// runs them in deterministic order (enum order: AuthenticatedUser, Qr, then
/// reserved future methods), short-circuits on the first failure, and fails
/// closed when a required method has no registered verifier.
/// Writes no attendance state and never logs the raw token (§7.4).
/// </summary>
public class AttendanceVerificationEngine : IAttendanceVerificationEngine
{
    private readonly IReadOnlyDictionary<AttendanceVerificationMethod, IAttendanceVerifier> _verifiers;
    private readonly ILogger<AttendanceVerificationEngine> _logger;

    public AttendanceVerificationEngine(
        IEnumerable<IAttendanceVerifier> verifiers,
        ILogger<AttendanceVerificationEngine> logger)
    {
        // Duplicate registrations are a wiring bug: fail fast at composition.
        _verifiers = verifiers.ToDictionary(v => v.Method);
        _logger = logger;
    }

    public async Task<ApiResponse<AttendanceVerificationResult>> VerifyAsync(
        AttendanceVerificationContext context)
    {
        // 1. Which methods does the policy demand? Deterministic order:
        //    enum declaration order = AuthenticatedUser, Qr, then reserved.
        var required = AttendanceVerification
            .ParseRequiredMethods(context.Policy.RequiredVerificationsJson)
            .OrderBy(m => (int)m)
            .ToList();

        if (required.Count == 0)
            return Fail(context, null, "VERIFICATION_METHOD_UNAVAILABLE",
                "The attendance policy requires no runnable verification method. Contact the business owner.",
                Array.Empty<string>());

        // 2. Fail closed on any required-but-unregistered method — checked up
        //    front so a misconfigured policy can never half-verify.
        var unavailable = required
            .Where(m => !_verifiers.ContainsKey(m))
            .Select(AttendanceVerification.WireValue)
            .ToList();
        if (unavailable.Count > 0)
            return Fail(context, null, "VERIFICATION_METHOD_UNAVAILABLE",
                $"Attendance policy requires unimplemented verification: {string.Join(", ", unavailable)}.",
                Array.Empty<string>());

        // 3. Run in order, short-circuit on first failure (§7.4: the engine
        //    never continues past a failure).
        var summaries = new List<string>();
        AttendanceQrCredential? credential = null;
        AttendanceLocation? location = null;

        foreach (var method in required)
        {
            var outcome = await _verifiers[method].VerifyAsync(context);
            summaries.Add(outcome.SummaryJson);

            if (!outcome.Passed)
                return Fail(context, method,
                    outcome.ErrorCode ?? "VERIFICATION_FAILED",
                    outcome.Message ?? "Verification failed.",
                    summaries);

            if (outcome.Credential != null) credential = outcome.Credential;
            if (outcome.Location != null) location = outcome.Location;
        }

        return ApiResponse<AttendanceVerificationResult>.Ok(
            AttendanceVerificationResult.Success(summaries, credential, location));
    }

    /// <summary>
    /// Builds the failure envelope and logs it (plan §19.4: a rejected
    /// verification is a Warning). Ids only — never the token or its hash.
    /// </summary>
    private ApiResponse<AttendanceVerificationResult> Fail(
        AttendanceVerificationContext context,
        AttendanceVerificationMethod? method,
        string code,
        string message,
        IReadOnlyList<string> summaries)
    {
        var failure = AttendanceVerificationResult.Failure(method, code, message, summaries);

        _logger.LogWarning(
            "Attendance verification rejected: business={BusinessId} actor={ActorUserId} event={EventType} method={Method} code={Code} steps={Steps}",
            context.BusinessId,
            context.ActorUserId,
            AttendanceVerification.WireValue(context.EventType),
            failure.Method.HasValue ? AttendanceVerification.WireValue(failure.Method.Value) : "POLICY",
            failure.FailureCode,
            failure.Summaries.Count);

        return ApiResponse<AttendanceVerificationResult>.Fail(
            failure.FailureCode!, failure.FailureMessage!);
    }
}