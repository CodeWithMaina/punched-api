using PunchedApi.Application.DTOs;

namespace PunchedApi.Domain.Interfaces;

/// <summary>
/// Staff invitation lifecycle service.
/// Staff can only be onboarded through a valid invitation — there is no public staff registration.
/// Every business-scoped operation derives the business from the authenticated user (tenant isolation),
/// never from a client-supplied business id.
/// </summary>
public interface IInvitationService
{
    /// <summary>
    /// Creates a pending staff invitation for an email owned by the given business owner,
    /// and emails the invitee a secure acceptance link.
    /// </summary>
    Task<ApiResponse<StaffInvitationResponse>> CreateStaffInvitationAsync(Guid businessOwnerId, CreateStaffInvitationRequest request);

    /// <summary>
    /// Lists all staff invitations for the caller's business (including accepted/revoked), oldest-first.
    /// </summary>
    Task<ApiResponse<List<StaffInvitationResponse>>> ListStaffInvitationsAsync(Guid businessOwnerId);

    /// <summary>
    /// Resends a pending invitation by rotating its token and refreshing its expiry.
    /// </summary>
    Task<ApiResponse<StaffInvitationResponse>> ResendStaffInvitationAsync(Guid businessOwnerId, Guid invitationId);

    /// <summary>
    /// Revokes a pending invitation.
    /// </summary>
    Task<ApiResponse<MessageResponse>> RevokeStaffInvitationAsync(Guid businessOwnerId, Guid invitationId);

    /// <summary>
    /// Publicly validates an invitation token and returns safe info for the acceptance page
    /// (business name, invited email, expiry). Returns valid=false with an error code when unusable.
    /// </summary>
    Task<ApiResponse<StaffInvitationValidationResponse>> ValidateStaffInvitationAsync(string token);

    /// <summary>
    /// Publicly accepts a valid invitation: verifies ownership, creates the staff account,
    /// links them to the invitation's business, marks the invitation accepted, and authenticates.
    /// </summary>
    /// <remarks>
    /// Only for invitees without an identity. If an account already exists for the invited
    /// address, this returns <c>EMAIL_IN_USE</c> — the token alone is not proof the caller
    /// controls that mailbox. Such users must sign in and call
    /// <see cref="AcceptStaffInvitationForExistingAccountAsync"/>, where the session is the
    /// ownership proof.
    /// </remarks>
    Task<ApiResponse<AuthResponse>> AcceptStaffInvitationAsync(string token, AcceptStaffInvitationRequest request);

    /// <summary>
    /// Accepts a valid invitation for an already-authenticated identity (the "I already have
    /// an account" path).
    /// </summary>
    /// <param name="userAuthId">Id of the signed-in identity (JWT <c>sub</c>), never a client-supplied user id.</param>
    /// <param name="token">The invitation token from the emailed link.</param>
    /// <remarks>
    /// Requires the identity's email to equal the invitation's invited email (<c>EMAIL_MISMATCH</c>),
    /// the invitation to be pending and unexpired, and the identity to hold no conflicting
    /// membership (<c>ALREADY_STAFF</c>, <c>ALREADY_MEMBER</c>, <c>OWNS_BUSINESS</c>,
    /// <c>ROLE_NOT_ELIGIBLE</c>). Staff may hold memberships in several businesses, so a staff
    /// role elsewhere is not a conflict. On success an <c>Active Staff</c> membership is granted
    /// for the inviting business, the invitation is marked accepted (single-use, DB-enforced via
    /// the status concurrency token) and tenant-aware tokens are issued.
    /// </remarks>
    Task<ApiResponse<AuthResponse>> AcceptStaffInvitationForExistingAccountAsync(Guid userAuthId, string token);
}