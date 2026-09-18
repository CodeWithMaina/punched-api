using Microsoft.EntityFrameworkCore;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Attendance.Verification;

/// <summary>
/// V1 verifier #2 (plan §7.3, §8): proves "the presented QR identifies an
/// active attendance point of THIS business". Looks up the credential by its
/// SHA-256 hash only — the raw token is never stored, logged or returned.
/// Unknown and malformed tokens deliberately share one code and one message
/// (INVALID_QR) so a scanner cannot be used as a probing oracle.
/// Writes no attendance state.
/// </summary>
public class QrVerifier : IAttendanceVerifier
{
    /// <summary>Hard bound on accepted payloads (the Phase 3 validator also enforces ≤ 512).</summary>
    private const int MaxPayloadLength = 512;

    private readonly ApplicationDbContext _context;

    public QrVerifier(ApplicationDbContext context)
    {
        _context = context;
    }

    public AttendanceVerificationMethod Method => AttendanceVerificationMethod.Qr;

    public async Task<AttendanceVerificationOutcome> VerifyAsync(AttendanceVerificationContext context)
    {
        var summary = AttendanceVerification.BuildSummary(
            AttendanceVerification.WireValue(Method), false, context.NowUtc);

        // Token present, namespaced and length-bounded. The prefix check lets a
        // foreign code be rejected before any database work (plan §8.2).
        if (string.IsNullOrWhiteSpace(context.CredentialToken) ||
            context.CredentialToken.Length > MaxPayloadLength ||
            !AttendanceTokenFactory.HasValidPrefix(context.CredentialToken))
            return AttendanceVerificationOutcome.Fail("INVALID_QR", "QR code is invalid.", summary);

        var tokenHash = AttendanceTokenFactory.HashToken(context.CredentialToken);

        // Hash-only lookup on the unique index. Deliberately NOT filtered by
        // business: a foreign credential must answer QR_WRONG_ORGANIZATION
        // (actionable), not INVALID_QR (§9.6 / V5).
        var credential = await _context.AttendanceQrCredentials
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.TokenHash == tokenHash);

        // Unknown ⇒ the same INVALID_QR as malformed (V3).
        if (credential == null)
            return AttendanceVerificationOutcome.Fail("INVALID_QR", "QR code is invalid.", summary);

        // Organisation scope (V5). The credential's business must equal the
        // server-resolved scope — the raw payload carries no business id.
        if (credential.BusinessId != context.BusinessId)
            return AttendanceVerificationOutcome.Fail(
                "QR_WRONG_ORGANIZATION", "This QR code belongs to a different business.", summary);

        // Lifecycle (V4): only an Active credential scans. Superseded and
        // explicitly revoked hashes are retained so an old sheet produces the
        // actionable QR_REVOKED rather than a confusing INVALID_QR.
        if (credential.Status != AttendanceCredentialStatus.Active)
            return AttendanceVerificationOutcome.Fail(
                "QR_REVOKED", "This QR code has been replaced. Ask for the current one.", summary);

        // No navigation property exists on the credential (Phase 1 keeps FK
        // columns only), so the location is loaded explicitly.
        var location = await _context.AttendanceLocations
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == credential.AttendanceLocationId);

        if (location == null)
            return AttendanceVerificationOutcome.Fail("INVALID_QR", "QR code is invalid.", summary);

        // Location lifecycle (V6) + defence in depth on organisation scope.
        if (location.BusinessId != context.BusinessId)
            return AttendanceVerificationOutcome.Fail(
                "QR_WRONG_ORGANIZATION", "This QR code belongs to a different business.", summary);

        if (!location.IsActive)
            return AttendanceVerificationOutcome.Fail(
                "LOCATION_INACTIVE", "This attendance point is currently disabled.", summary);

        var passed = AttendanceVerification.BuildSummary(
            AttendanceVerification.WireValue(Method), true, context.NowUtc);
        return AttendanceVerificationOutcome.Pass(passed, credential, location);
    }
}