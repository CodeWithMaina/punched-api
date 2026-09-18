using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.Authorization;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Attendance.Verification;

/// <summary>
/// V1 verifier #1 (plan §7.3): proves "the actor is a live, linked,
/// attendance-authorized user". Identity comes only from the JWT-derived
/// context — there is no code path through which a client could supply the
/// staff identity. Writes no attendance state.
/// </summary>
public class AuthenticatedUserVerifier : IAttendanceVerifier
{
    private readonly ApplicationDbContext _context;

    public AuthenticatedUserVerifier(ApplicationDbContext context)
    {
        _context = context;
    }

    public AttendanceVerificationMethod Method => AttendanceVerificationMethod.AuthenticatedUser;

    public async Task<AttendanceVerificationOutcome> VerifyAsync(AttendanceVerificationContext context)
    {
        var summary = AttendanceVerification.BuildSummary(
            AttendanceVerification.WireValue(Method), false, context.NowUtc);

        // The JWT userId claim is the ONLY source of identity; it must be
        // present and non-empty (the service refuses to build a context
        // without it — re-checked here as defence in depth).
        if (context.ActorUserId == Guid.Empty)
            return AttendanceVerificationOutcome.Fail(
                "UNAUTHORIZED", "No authenticated user.", summary);

        // The User global query filter (IsDeleted) hides soft-deleted rows, so
        // a deactivated staff member resolves to null here and is mapped to
        // FORBIDDEN — the §9.6 contract ("deleted staff ⇒ FORBIDDEN", one
        // clear message for missing and deleted alike).
        var actor = await _context.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == context.ActorUserId);

        if (actor == null)
            return AttendanceVerificationOutcome.Fail(
                "FORBIDDEN", "Your account is not active.", summary);

        if (actor.Role is not (UserRole.Staff or UserRole.Business))
            return AttendanceVerificationOutcome.Fail(
                "FORBIDDEN", "Only business owners and staff can record attendance.", summary);

        // Fine-grained permission from the static matrix (catalog-backed,
        // built from ModuleCatalog at startup). The DATABASE role is the
        // authority — a stale role claim can never widen access.
        if (!PermissionMatrix.HasPermission(actor.Role.ToString(), "attendance.clock"))
            return AttendanceVerificationOutcome.Fail(
                "FORBIDDEN", "You do not have permission to record attendance.", summary);

        // For staff, the linked business must exist AND match the server-resolved
        // scope. Null StaffBusinessId ⇒ NOT_LINKED (§9.6 / V9).
        if (actor.Role == UserRole.Staff)
        {
            if (actor.StaffBusinessId == null)
                return AttendanceVerificationOutcome.Fail(
                    "NOT_LINKED", "Staff user is not linked to a business.", summary);

            if (actor.StaffBusinessId != context.BusinessId)
                return AttendanceVerificationOutcome.Fail(
                    "FORBIDDEN_SCOPE", "You are not authorized for this business.", summary);
        }

        var passed = AttendanceVerification.BuildSummary(
            AttendanceVerification.WireValue(Method), true, context.NowUtc);
        return AttendanceVerificationOutcome.Pass(passed);
    }
}
