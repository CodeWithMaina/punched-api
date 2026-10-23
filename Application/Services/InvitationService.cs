using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Settings;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;

namespace PunchedApi.Application.Services;

/// <summary>
/// Implements the invitation-only staff onboarding lifecycle.
///
/// Security invariants enforced here:
///   • Business context is always derived from the authenticated owner (never a client id).
///   • Invitation tokens are cryptographically random and only hashed in storage.
///   • Accepted/revoked/expired invitations cannot be reused.
///   • Acceptance is locked to the invited email (ownership confirmed server-side).
///   • An existing account is never silently attached — ownership is a hard requirement.
/// </summary>
public class InvitationService : IInvitationService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly JwtTokenService _jwtService;
    private readonly IEmailService _emailService;
    private readonly PublicAppSettings _publicApp;
    private readonly ILogger<InvitationService> _logger;

    /// <summary>Canonical membership writer — invitation acceptance is a membership grant.</summary>
    private readonly IBusinessMembershipService _memberships;

    public InvitationService(
        IUnitOfWork unitOfWork,
        JwtTokenService jwtService,
        IEmailService emailService,
        IOptions<PublicAppSettings> publicApp,
        ILogger<InvitationService> logger,
        IBusinessMembershipService? memberships = null)
    {
        _unitOfWork = unitOfWork;
        _jwtService = jwtService;
        _emailService = emailService;
        _publicApp = publicApp.Value;
        _logger = logger;
        _memberships = memberships ?? new BusinessMembershipService(unitOfWork);
    }

    /// <inheritdoc />
    public async Task<ApiResponse<StaffInvitationResponse>> CreateStaffInvitationAsync(
        Guid businessOwnerId, CreateStaffInvitationRequest request)
    {
        try
        {
            var business = await GetOwnedBusinessAsync(businessOwnerId);
            if (business == null)
                return ApiResponse<StaffInvitationResponse>.Fail("NOT_FOUND", "No business found for this account.");

            var email = request.Email.Trim().ToLowerInvariant();

            // Prevent duplicate pending invitations for the same business+email.
            var existingPending = await _unitOfWork.StaffInvitations.FirstOrDefaultAsync(i =>
                i.BusinessId == business.Id && i.InvitedEmail == email && i.Status == InvitationStatus.Pending);

            if (existingPending != null)
            {
                _logger.LogWarning("Duplicate staff invitation attempt for {Email} at business {BusinessId}", email, business.Id);
                return ApiResponse<StaffInvitationResponse>.Fail(
                    "DUPLICATE_INVITATION",
                    "An invitation to this email is already pending.");
            }

            var token = GenerateToken();
            var invitation = new StaffInvitation
            {
                Id = Guid.NewGuid(),
                BusinessId = business.Id,
                InvitedEmail = email,
                InvitingUserId = businessOwnerId,
                TokenHash = HashToken(token),
                Status = InvitationStatus.Pending,
                ExpiresAt = DateTime.UtcNow.AddDays(_publicApp.InvitationExpiryDays),
                ResendCount = 0,
                CreatedAt = DateTime.UtcNow
            };

            try
            {
                await _unitOfWork.StaffInvitations.AddAsync(invitation);
                await _unitOfWork.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                // Concurrent duplicate insert — the filtered unique index won.
                return ApiResponse<StaffInvitationResponse>.Fail(
                    "DUPLICATE_INVITATION",
                    "An invitation to this email is already pending.");
            }

            await SendInvitationEmailAsync(invitation, token);

            _logger.LogInformation("Staff invitation created by owner {Owner} for {Email} at business {Business}",
                businessOwnerId, email, business.Id);

            return ApiResponse<StaffInvitationResponse>.Ok(MapToResponse(invitation));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating staff invitation for owner {OwnerId}", businessOwnerId);
            return ApiResponse<StaffInvitationResponse>.Fail("CREATE_FAILED", "Failed to create the invitation.");
        }
    }
    /// <inheritdoc />
    public async Task<ApiResponse<List<StaffInvitationResponse>>> ListStaffInvitationsAsync(Guid businessOwnerId)
    {
        var business = await GetOwnedBusinessAsync(businessOwnerId);
        if (business == null)
            return ApiResponse<List<StaffInvitationResponse>>.Fail("NOT_FOUND", "No business found for this account.");

        var invitations = (await _unitOfWork.StaffInvitations.FindAsync(i => i.BusinessId == business.Id))
            .OrderByDescending(i => i.CreatedAt)
            .Select(MapToResponse)
            .ToList();

        return ApiResponse<List<StaffInvitationResponse>>.Ok(invitations);
    }

    /// <inheritdoc />
    public async Task<ApiResponse<StaffInvitationResponse>> ResendStaffInvitationAsync(Guid businessOwnerId, Guid invitationId)
    {
        var business = await GetOwnedBusinessAsync(businessOwnerId);
        if (business == null)
            return ApiResponse<StaffInvitationResponse>.Fail("NOT_FOUND", "No business found for this account.");

        var invitation = await _unitOfWork.StaffInvitations.FirstOrDefaultAsync(i =>
            i.Id == invitationId && i.BusinessId == business.Id);

        if (invitation == null)
            return ApiResponse<StaffInvitationResponse>.Fail("NOT_FOUND", "Invitation not found.");

        if (invitation.Status != InvitationStatus.Pending)
            return ApiResponse<StaffInvitationResponse>.Fail("INVITATION_NOT_ACTIVE", "Only pending invitations can be re-sent.");

        // Rotate the token and refresh the expiry on every resend.
        var token = GenerateToken();
        invitation.TokenHash = HashToken(token);
        invitation.ExpiresAt = DateTime.UtcNow.AddDays(_publicApp.InvitationExpiryDays);
        invitation.ResendCount += 1;
        _unitOfWork.StaffInvitations.Update(invitation);
        await _unitOfWork.SaveChangesAsync();

        await SendInvitationEmailAsync(invitation, token);

        _logger.LogInformation("Re-sent staff invitation {InvitationId} for {Email}", invitation.Id, invitation.InvitedEmail);
        return ApiResponse<StaffInvitationResponse>.Ok(MapToResponse(invitation));
    }

    /// <inheritdoc />
    public async Task<ApiResponse<MessageResponse>> RevokeStaffInvitationAsync(Guid businessOwnerId, Guid invitationId)
    {
        var business = await GetOwnedBusinessAsync(businessOwnerId);
        if (business == null)
            return ApiResponse<MessageResponse>.Fail("NOT_FOUND", "No business found for this account.");

        var invitation = await _unitOfWork.StaffInvitations.FirstOrDefaultAsync(i =>
            i.Id == invitationId && i.BusinessId == business.Id);

        if (invitation == null)
            return ApiResponse<MessageResponse>.Fail("NOT_FOUND", "Invitation not found.");

        if (invitation.Status == InvitationStatus.Accepted)
            return ApiResponse<MessageResponse>.Fail("ALREADY_ACCEPTED", "This invitation has already been accepted and cannot be revoked.");

        if (invitation.Status != InvitationStatus.Revoked)
        {
            invitation.Status = InvitationStatus.Revoked;
            invitation.RevokedAt = DateTime.UtcNow;
            _unitOfWork.StaffInvitations.Update(invitation);
            await _unitOfWork.SaveChangesAsync();
        }

        _logger.LogInformation("Staff invitation {InvitationId} revoked by owner {Owner}", invitationId, businessOwnerId);
        return ApiResponse<MessageResponse>.Ok(new MessageResponse { Message = "Invitation revoked." });
    }

    /// <inheritdoc />
    public async Task<ApiResponse<StaffInvitationValidationResponse>> ValidateStaffInvitationAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return Invalid("INVALID_TOKEN", "This invitation link is invalid.");

        var hash = HashToken(token.Trim());
        var invitation = await _unitOfWork.StaffInvitations.FirstOrDefaultAsync(i => i.TokenHash == hash);

        if (invitation == null)
            return Invalid("INVALID_TOKEN", "This invitation link is invalid or has already been used.");

        var business = await GetInvitableBusinessAsync(invitation);
        if (business == null)
            return Invalid("BUSINESS_NOT_FOUND", "The business that issued this invitation is no longer available.");

        if (LifecycleError(invitation) is { } lifecycle)
            return Invalid(lifecycle.Code, lifecycle.Message);

        return ApiResponse<StaffInvitationValidationResponse>.Ok(new StaffInvitationValidationResponse
        {
            Valid = true,
            BusinessId = invitation.BusinessId,
            BusinessName = business.Name,
            BusinessLogoUrl = business.LogoUrl,
            Email = invitation.InvitedEmail,
            ExpiresAt = invitation.ExpiresAt
        });
    }
    /// <inheritdoc />
    public async Task<ApiResponse<AuthResponse>> AcceptStaffInvitationAsync(string token, AcceptStaffInvitationRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(token))
                return ApiResponse<AuthResponse>.Fail("INVALID_TOKEN", "This invitation link is invalid.");

            var invitation = await _unitOfWork.StaffInvitations.FirstOrDefaultAsync(i => i.TokenHash == HashToken(token.Trim()));

            if (invitation == null)
                return ApiResponse<AuthResponse>.Fail("INVALID_TOKEN", "This invitation link is invalid or has already been used.");

            if (await GetInvitableBusinessAsync(invitation) == null)
                return ApiResponse<AuthResponse>.Fail("BUSINESS_NOT_FOUND", "The business that issued this invitation is no longer available.");

            if (LifecycleError(invitation) is { } lifecycle)
                return ApiResponse<AuthResponse>.Fail(lifecycle.Code, lifecycle.Message);

            // Acceptance is locked to the invited email — derived from the server-side record, never the client.
            var invitedEmail = invitation.InvitedEmail;
            if (!string.Equals(request.EmailConfirmation.Trim().ToLowerInvariant(), invitedEmail, StringComparison.OrdinalIgnoreCase))
                return ApiResponse<AuthResponse>.Fail("EMAIL_MISMATCH", "The email you confirmed does not match the invited address.");

            // An existing identity is NEVER attached to a business by this public
            // endpoint (the token alone is not proof the caller controls the
            // mailbox). It must sign in and accept explicitly via
            // POST /v1/invitations/{token}/accept-existing, where the session
            // proves ownership of the invited address.
            var existingAuth = await _unitOfWork.UserAuths.FirstOrDefaultAsync(a => a.Email == invitedEmail);
            if (existingAuth != null)
            {
                var existingUser = await _unitOfWork.Users.FirstOrDefaultAsync(u => u.Email == invitedEmail);
                if (existingUser != null &&
                    await _memberships.IsActiveMemberAsync(existingUser.Id, invitation.BusinessId) &&
                    await _memberships.HasRoleAnywhereAsync(existingUser.Id, BusinessMembershipRole.Staff))
                    return ApiResponse<AuthResponse>.Fail("ALREADY_STAFF", "You are already linked to this business as staff.");

                return ApiResponse<AuthResponse>.Fail(
                    "EMAIL_IN_USE",
                    "An account with this email already exists. Sign in and open the invitation link again to join as staff.");
            }

            var passwordHash = BCrypt.Net.BCrypt.HashPassword(request.Password, 12);

            var userAuth = new UserAuth
            {
                Id = Guid.NewGuid(),
                Email = invitedEmail,
                PasswordHash = passwordHash,
                IsVerified = true,
                FailedLoginAttempts = 0,
                VerificationCode = null,
                VerificationCodeExpiresAt = null,
                VerificationCodeAttempts = 0,
                CreatedAt = DateTime.UtcNow,
                LastLoginAt = DateTime.UtcNow
            };
            await _unitOfWork.UserAuths.AddAsync(userAuth);

            var user = new User
            {
                Id = Guid.NewGuid(),
                Email = invitedEmail,
                FullName = request.FullName.Trim(),
                // Persona hint for UI routing; the grant below is the membership.
                Role = UserRole.Staff,
                CreatedAt = DateTime.UtcNow
            };
            await _unitOfWork.Users.AddAsync(user);

            // The invitation IS the membership grant. Same unit of work as the
            // identity and the invitation status change: a committed staff
            // account always has its membership and a consumed token.
            await _memberships.EnsureAsync(
                user.Id,
                invitation.BusinessId,
                BusinessMembershipRole.Staff,
                invitation.InvitingUserId);

            invitation.Status = InvitationStatus.Accepted;
            invitation.AcceptedAt = DateTime.UtcNow;
            _unitOfWork.StaffInvitations.Update(invitation);

            // Issue tenant-aware tokens so the staff member is authenticated
            // immediately AND carries the tenant context (biz + bizRole=Staff),
            // exactly like a tenant-aware sign-in would. Without these claims the
            // new staff session would be treated as tenant-less on every
            // business-scoped request until the first refresh.
            var accessToken = _jwtService.GenerateAccessToken(
                userAuth, user, invitation.BusinessId, TenantClaimNames.RoleStaff);
            var refreshTokenValue = _jwtService.GenerateRefreshToken();
            await _unitOfWork.RefreshTokens.AddAsync(new RefreshToken
            {
                Id = Guid.NewGuid(),
                UserAuthId = userAuth.Id,
                Token = refreshTokenValue,
                ExpiresAt = _jwtService.RefreshTokenExpiry,
                CreatedAt = DateTime.UtcNow
            });

            try
            {
                await _unitOfWork.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                // Concurrent acceptance won the row (Status is a concurrency
                // token). The account created above was never committed, so the
                // invitation remains single-use and exactly one staff account
                // exists.
                _logger.LogWarning("Concurrent acceptance of staff invitation {InvitationId}", invitation.Id);
                return ApiResponse<AuthResponse>.Fail("ALREADY_USED", "This invitation has already been accepted.");
            }

            _logger.LogInformation("Staff invitation {InvitationId} accepted for {Email} at business {Business}",
                invitation.Id, invitedEmail, invitation.BusinessId);

            return ApiResponse<AuthResponse>.Ok(new AuthResponse
            {
                AccessToken = accessToken,
                RefreshToken = refreshTokenValue,
                ExpiresIn = _jwtService.AccessTokenExpirySeconds,
                User = new UserProfileResponse
                {
                    Id = user.Id,
                    Email = user.Email,
                    Phone = user.PhoneNumber,
                    FullName = user.FullName,
                    AvatarUrl = user.AvatarUrl,
                    Role = user.Role,
                    CreatedAt = user.CreatedAt
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error accepting staff invitation");
            return ApiResponse<AuthResponse>.Fail("ACCEPT_FAILED", "Failed to accept the invitation. Please try again.");
        }
    }
    /// <inheritdoc />
    public async Task<ApiResponse<AuthResponse>> AcceptStaffInvitationForExistingAccountAsync(Guid userAuthId, string token)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(token))
                return ApiResponse<AuthResponse>.Fail("INVALID_TOKEN", "This invitation link is invalid.");

            var invitation = await _unitOfWork.StaffInvitations
                .FirstOrDefaultAsync(i => i.TokenHash == HashToken(token.Trim()));

            if (invitation == null)
                return ApiResponse<AuthResponse>.Fail("INVALID_TOKEN", "This invitation link is invalid or has already been used.");

            if (await GetInvitableBusinessAsync(invitation) == null)
                return ApiResponse<AuthResponse>.Fail("BUSINESS_NOT_FOUND", "The business that issued this invitation is no longer available.");

            if (LifecycleError(invitation) is { } lifecycle)
                return ApiResponse<AuthResponse>.Fail(lifecycle.Code, lifecycle.Message);

            // Ownership proof for an existing identity is the session itself: the
            // signed-in account's email must equal the invited address. The
            // invitation token proves the owner invited that address; the session
            // proves this caller controls it.
            var auth = await _unitOfWork.UserAuths.FirstOrDefaultAsync(a => a.Id == userAuthId);
            if (auth == null)
                return ApiResponse<AuthResponse>.Fail("UNAUTHORIZED", "Invalid session.");

            var user = await _unitOfWork.Users.FirstOrDefaultAsync(u => u.Email == auth.Email);
            if (user == null || user.IsDeleted)
                return ApiResponse<AuthResponse>.Fail("ACCOUNT_DISABLED", "This account is not available.");

            if (!string.Equals(user.Email, invitation.InvitedEmail, StringComparison.OrdinalIgnoreCase))
                return ApiResponse<AuthResponse>.Fail(
                    "EMAIL_MISMATCH",
                    "You are signed in with a different email address than the one that was invited.");

            if (user.Role == UserRole.Admin)
                return ApiResponse<AuthResponse>.Fail(
                    "ROLE_NOT_ELIGIBLE", "Administrator accounts cannot be linked as business staff.");

            // Membership is the only relationship signal. Staff may belong to
            // several businesses (multi-tenant), so a staff role elsewhere is not
            // a conflict — but an active membership of THIS business already
            // exists, and there is nothing to accept twice.
            var existingMembership = await _memberships.FindAsync(user.Id, invitation.BusinessId);
            if (existingMembership is { Status: BusinessMembershipStatus.Active })
            {
                return existingMembership.Role == BusinessMembershipRole.Staff
                    ? ApiResponse<AuthResponse>.Fail("ALREADY_STAFF", "You are already linked to this business as staff.")
                    : ApiResponse<AuthResponse>.Fail(
                        "ALREADY_MEMBER",
                        "You already belong to this business. Nothing to accept.");
            }

            // An owner is a tenant of their own business; the v1 product rule is
            // one owned business per identity, so an owner cannot also be staff.
            if (await _memberships.HasRoleAnywhereAsync(user.Id, BusinessMembershipRole.Owner))
                return ApiResponse<AuthResponse>.Fail(
                    "OWNS_BUSINESS",
                    "This account owns a business and cannot also join another business as staff. Use a different email address.");

            // The membership grant rides the same commit as the invitation status
            // change and the token row, so acceptance is atomic and single-use.
            await _memberships.EnsureAsync(
                user.Id,
                invitation.BusinessId,
                BusinessMembershipRole.Staff,
                invitation.InvitingUserId);

            // Persona stays a routing hint derived from membership.
            if (user.Role != UserRole.Business)
            {
                user.Role = UserRole.Staff;
                _unitOfWork.Users.Update(user);
            }

            invitation.Status = InvitationStatus.Accepted;
            invitation.AcceptedAt = DateTime.UtcNow;
            _unitOfWork.StaffInvitations.Update(invitation);

            var accessToken = _jwtService.GenerateAccessToken(
                auth, user, invitation.BusinessId, TenantClaimNames.RoleStaff);
            var refreshTokenValue = _jwtService.GenerateRefreshToken();
            await _unitOfWork.RefreshTokens.AddAsync(new RefreshToken
            {
                Id = Guid.NewGuid(),
                UserAuthId = auth.Id,
                Token = refreshTokenValue,
                ExpiresAt = _jwtService.RefreshTokenExpiry,
                CreatedAt = DateTime.UtcNow
            });

            try
            {
                await _unitOfWork.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                _logger.LogWarning("Concurrent acceptance of staff invitation {InvitationId}", invitation.Id);
                return ApiResponse<AuthResponse>.Fail("ALREADY_USED", "This invitation has already been accepted.");
            }

            _logger.LogInformation(
                "Existing identity {UserId} accepted staff invitation {InvitationId} at business {BusinessId}",
                user.Id, invitation.Id, invitation.BusinessId);

            return ApiResponse<AuthResponse>.Ok(new AuthResponse
            {
                AccessToken = accessToken,
                RefreshToken = refreshTokenValue,
                ExpiresIn = _jwtService.AccessTokenExpirySeconds,
                User = new UserProfileResponse
                {
                    Id = user.Id,
                    Email = user.Email,
                    Phone = user.PhoneNumber,
                    FullName = user.FullName,
                    AvatarUrl = user.AvatarUrl,
                    Role = user.Role,
                    CreatedAt = user.CreatedAt
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error accepting staff invitation for an existing account");
            return ApiResponse<AuthResponse>.Fail("ACCEPT_FAILED", "Failed to accept the invitation. Please try again.");
        }
    }

    // ── Private helpers ─────────────────────────────────────

    /// <summary>
    /// The business the caller OWNS, resolved from their active Owner
    /// membership (never from <c>Business.OwnerId</c>).
    /// </summary>
    private async Task<Business?> GetOwnedBusinessAsync(Guid ownerId)
    {
        var membership = await _unitOfWork.BusinessMemberships.FirstOrDefaultAsync(m =>
            m.UserId == ownerId &&
            m.Role == BusinessMembershipRole.Owner &&
            m.Status == BusinessMembershipStatus.Active);

        return membership == null
            ? null
            : await _unitOfWork.Businesses.GetByIdAsync(membership.BusinessId);
    }

    /// <summary>
    /// The tenant that issued the invitation must still exist — a deleted
    /// business must never grant staff access.
    /// </summary>
    private async Task<Business?> GetInvitableBusinessAsync(StaffInvitation invitation)
    {
        var business = await _unitOfWork.Businesses.GetByIdAsync(invitation.BusinessId);
        return business is { IsDeleted: false } ? business : null;
    }

    /// <summary>
    /// Single source of truth for the invitation lifecycle gate. Validation and
    /// both acceptance paths (new account + existing account) call this, so the
    /// public page, the public accept endpoint and the authenticated accept
    /// endpoint can never disagree about whether a token is still usable.
    /// </summary>
    private static (string Code, string Message)? LifecycleError(StaffInvitation invitation)
    {
        if (invitation.Status == InvitationStatus.Revoked)
            return ("REVOKED", "This invitation has been revoked.");

        if (invitation.Status == InvitationStatus.Accepted)
            return ("ALREADY_USED", "This invitation has already been accepted.");

        if (invitation.ExpiresAt < DateTime.UtcNow)
            return ("EXPIRED", "This invitation has expired.");

        return null;
    }

    private async Task SendInvitationEmailAsync(StaffInvitation invitation, string token)
    {
        var url = $"{_publicApp.BaseUrl.TrimEnd('/')}/invitations/accept?token={token}";
        var business = await _unitOfWork.Businesses.GetByIdAsync(invitation.BusinessId);
        var businessName = business?.Name ?? "the business";

        try
        {
            var ok = await _emailService.SendStaffInvitationAsync(invitation.InvitedEmail, businessName, url, invitation.ExpiresAt);
            if (!ok) _logger.LogWarning("Email provider reported a failure sending invitation to {Email}", invitation.InvitedEmail);
        }
        catch (Exception ex)
        {
            // The invitation is persisted; an email delivery failure must not fail the enclosing action.
            _logger.LogWarning(ex, "Failed to send invitation email to {Email}", invitation.InvitedEmail);
        }
    }

    private static string GenerateToken()
        => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private static string HashToken(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static StaffInvitationResponse MapToResponse(StaffInvitation i) => new()
    {
        Id = i.Id,
        BusinessId = i.BusinessId,
        Email = i.InvitedEmail,
        InvitedByUserId = i.InvitingUserId,
        Status = i.Status,
        StatusLabel = i.Status switch
        {
            InvitationStatus.Accepted => "Accepted",
            InvitationStatus.Revoked => "Revoked",
            _ => "Pending"
        },
        CreatedAt = i.CreatedAt,
        ExpiresAt = i.ExpiresAt,
        AcceptedAt = i.AcceptedAt,
        RevokedAt = i.RevokedAt,
        ResendCount = i.ResendCount,
        IsExpired = i.Status == InvitationStatus.Pending && i.ExpiresAt < DateTime.UtcNow
    };

    private static ApiResponse<StaffInvitationValidationResponse> Invalid(string code, string message)
        => ApiResponse<StaffInvitationValidationResponse>.Ok(new StaffInvitationValidationResponse
        {
            Valid = false,
            ErrorCode = code,
            ErrorMessage = message
        });
}