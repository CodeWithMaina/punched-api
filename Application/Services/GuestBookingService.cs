using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Notifications;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;
using PunchedApi.Infrastructure.Repositories;

namespace PunchedApi.Application.Services;

/// <summary>
/// Guest booking — see <see cref="IGuestBookingService"/>.
///
/// <para><b>Reuse, not a parallel path.</b> The visitor's identity is stored in
/// the same <c>users</c> + <c>user_auths</c> rows a registered customer has, the
/// one-time code lives in the same columns the account-verification flow uses,
/// and the customer's relationship to the business is the canonical
/// <c>business_memberships</c> row. There is no guest table, no guest customer
/// type and no second booking endpoint: once verified, the guest holds an
/// ordinary short-lived customer token and books through
/// <c>POST /v1/appointments</c>, so every existing rule applies.</para>
///
/// <para><b>What makes it safe.</b> A token exists only after a code that was
/// emailed to the address on the record has been presented and accepted. Codes
/// expire, are attempt-limited, are stored hashed, and are compared in constant
/// time. A resend is rate-limited by a cooldown. An address that already belongs
/// to a verified account is refused rather than verified, so this flow can never
/// become an account-takeover path.</para>
/// </summary>
public sealed class GuestBookingService : IGuestBookingService
{
    /// <summary>The module that owns booking; the storefront only offers what the business bought.</summary>
    private const string ModuleKey = "appointments";

    private const int CodeExpiryMinutes = 10;
    private const int MaxCodeAttempts = 5;
    private const int ResendCooldownSeconds = 60;
    private const int MaxNameLength = 100;

    /// <summary>Kenyan mobile numbers only, in the shapes a person types them.</summary>
    private static readonly Regex KenyanPhone = new(@"^(?:\+?254|0)?[17]\d{8}$", RegexOptions.Compiled);

    private static readonly Regex Email = new(
        @"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);

    private readonly ApplicationDbContext _db;
    private readonly IEmailService _email;
    private readonly INotificationService? _notifications;
    private readonly IModuleEntitlementService _modules;
    private readonly JwtTokenService _jwt;
    private readonly JwtSettings _jwtSettings;
    private readonly ITenantContext? _tenant;
    private readonly ILogger<GuestBookingService> _logger;

    /// <summary>
    /// Canonical membership writer. Optional so unit tests can construct the
    /// service without one; it is then built over the same DbContext (identical
    /// semantics — the service is stateless).
    /// </summary>
    private IBusinessMembershipService? _memberships;

    public GuestBookingService(
        ApplicationDbContext db,
        IEmailService email,
        IModuleEntitlementService modules,
        JwtTokenService jwt,
        IOptions<JwtSettings> jwtSettings,
        ILogger<GuestBookingService> logger,
        INotificationService? notifications = null,
        ITenantContext? tenant = null,
        IBusinessMembershipService? memberships = null)
    {
        _db = db;
        _email = email;
        _modules = modules;
        _jwt = jwt;
        _jwtSettings = jwtSettings.Value;
        _logger = logger;
        _notifications = notifications;
        _tenant = tenant;
        _memberships = memberships;
    }

    private Guid? TenantBusinessId => _tenant?.IsActive == true ? _tenant.BusinessId : null;

    /// <summary>Normalises any Kenyan shape to `2547XXXXXXXX`/`2541XXXXXXXX`, or null.</summary>
    internal static string? NormalisePhone(string? input)
    {
        var digits = Regex.Replace((input ?? string.Empty).Trim(), @"[\s\-()]", string.Empty);
        return KenyanPhone.IsMatch(digits) ? $"254{digits[^9..]}" : null;
    }

    /// <summary>`peter@example.com` → `p•••@example.com`.</summary>
    private static string MaskEmail(string email)
    {
        var at = email.IndexOf('@');
        if (at <= 0) return "•••";
        var local = email[..at];
        var head = local.Length <= 1 ? local : local[..1];
        return $"{head}•••{email[at..]}";
    }

    public async Task<ApiResponse<StartGuestBookingResponse>> StartAsync(
        Guid businessId, StartGuestBookingRequest request, CancellationToken cancellationToken = default)
    {
        if (businessId == Guid.Empty)
            return ApiResponse<StartGuestBookingResponse>.Fail("NOT_FOUND", "Business not found.");

        if (TenantBusinessId is Guid activeTenantId && businessId != activeTenantId)
            return ApiResponse<StartGuestBookingResponse>.Fail("NOT_FOUND", "Business not found.");

        var name = (request.FullName ?? string.Empty).Trim();
        if (name.Length < 2 || name.Length > MaxNameLength)
            return ApiResponse<StartGuestBookingResponse>.Fail(
                "VALIDATION_ERROR", $"Please give a name between 2 and {MaxNameLength} characters.");

        var phone = NormalisePhone(request.PhoneNumber);
        if (phone == null)
            return ApiResponse<StartGuestBookingResponse>.Fail(
                "VALIDATION_ERROR", "Use a Kenyan phone number, for example 0712 345 678.");

        var email = (request.Email ?? string.Empty).Trim().ToLowerInvariant();
        if (email.Length > 255 || !Email.IsMatch(email))
            return ApiResponse<StartGuestBookingResponse>.Fail(
                "VALIDATION_ERROR", "Enter a valid email address — the code is sent there.");

        var business = await _db.Businesses.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == businessId && !b.IsDeleted, cancellationToken);
        if (business == null)
            return ApiResponse<StartGuestBookingResponse>.Fail("NOT_FOUND", "Business not found.");

        // Same effective-entitlement lookup the owner surfaces use, applied to the
        // business the request named — an anonymous caller has no token for
        // [RequireModule] to resolve.
        if (!await _modules.IsModuleEnabledAsync(businessId, ModuleKey))
            return ApiResponse<StartGuestBookingResponse>.Fail(
                "MODULE_DISABLED", "This business does not take bookings through Punched.");

        try
        {
            var identity = await ResolveOrCreateIdentityAsync(businessId, name, phone, email, cancellationToken);
            if (!identity.Success)
                return ApiResponse<StartGuestBookingResponse>.Fail(identity.Error!.Code, identity.Error.Message);

            var auth = identity.Data!.Auth;
            if (auth is null)
                return ApiResponse<StartGuestBookingResponse>.Fail(
                    "ACCOUNT_EXISTS", "That email already has a Punched account. Please sign in to book.");

            // A resend is a cooldown, not a failure: the visitor is told when to
            // try again rather than being left to hammer the endpoint.
            var issuedAt = auth.VerificationCodeExpiresAt?.AddMinutes(-CodeExpiryMinutes);
            if (issuedAt.HasValue && issuedAt.Value.AddSeconds(ResendCooldownSeconds) > DateTime.UtcNow)
            {
                var wait = (int)Math.Ceiling(
                    (issuedAt.Value.AddSeconds(ResendCooldownSeconds) - DateTime.UtcNow).TotalSeconds);
                return ApiResponse<StartGuestBookingResponse>.Fail(
                    "RESEND_TOO_SOON", $"A code was just sent. Try again in {Math.Max(wait, 1)} seconds.");
            }

            var code = OneTimeCode.Generate();
            auth.VerificationCode = OneTimeCode.Hash(code);
            auth.VerificationCodeExpiresAt = DateTime.UtcNow.AddMinutes(CodeExpiryMinutes);
            auth.VerificationCodeAttempts = 0;
            // No Update(): the row is already tracked — Added when the identity was
            // just created, Unchanged when it was loaded — and calling Update() on a
            // not-yet-saved row would flip it to Modified and turn the INSERT into an
            // UPDATE of nothing.

            await _db.SaveChangesAsync(cancellationToken);

            // Delivery happens after the hash is durable: a code that cannot be
            // verified is worse than one that arrives a moment later.
            await SendCodeAsync(identity.Data, email, code, cancellationToken);

            _logger.LogInformation(
                "Guest booking code issued for business {BusinessId} to {Email}", businessId, MaskEmail(email));

            return ApiResponse<StartGuestBookingResponse>.Ok(new StartGuestBookingResponse
            {
                Email = MaskEmail(email),
                PhoneNumber = phone,
                ExpiresAt = auth.VerificationCodeExpiresAt!.Value,
                ResendAvailableAt = DateTime.UtcNow.AddSeconds(ResendCooldownSeconds)
            });
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Guest booking start failed for business {BusinessId}", businessId);
            return ApiResponse<StartGuestBookingResponse>.Fail(
                "GUEST_BOOKING_FAILED", "We could not send a code just now. Please try again.");
        }
    }

    /// <summary>
    /// Finds the customer behind the visitor's details, or creates the unverified
    /// record that will become one.
    ///
    /// <para>An address or number that already belongs to a <b>verified</b>
    /// account is refused: verifying it here would let anyone with inbox access
    /// mint a session for somebody else's account. Those visitors are told to
    /// sign in.</para>
    /// </summary>
    private async Task<ApiResponse<User>> ResolveOrCreateIdentityAsync(
        Guid businessId, string name, string phone, string email, CancellationToken cancellationToken)
    {
        // The email address is the verification channel, so it is the strongest
        // handle to resolve by.
        var byEmail = await _db.Users.Include(user => user.Auth)
            .FirstOrDefaultAsync(user => user.Email == email, cancellationToken);
        if (byEmail != null)
        {
            if (byEmail.Auth?.IsVerified == true)
                return ApiResponse<User>.Fail(
                    "ACCOUNT_EXISTS", "That email already has a Punched account. Please sign in to book.");

            byEmail.FullName = name;
            byEmail.PhoneNumber = phone;
            await EnsureCustomerMembershipAsync(businessId, byEmail.Id, cancellationToken);
            return ApiResponse<User>.Ok(byEmail);
        }

        // An earlier attempt may already have created an unverified record against
        // this number. It is reused only when it is the SAME identity — i.e. the
        // same verification address: re-pointing an existing record's email from
        // an anonymous request would let a caller move somebody else's unverified
        // record onto an address they control.
        var byPhone = await _db.Users.Include(user => user.Auth)
            .FirstOrDefaultAsync(user => user.PhoneNumber == phone, cancellationToken);
        if (byPhone != null)
        {
            if (byPhone.Auth?.IsVerified == true)
                return ApiResponse<User>.Fail(
                    "ACCOUNT_EXISTS", "That number already has a Punched account. Please sign in to book.");

            if (string.Equals(byPhone.Email, email, StringComparison.OrdinalIgnoreCase))
            {
                byPhone.FullName = name;
                await EnsureCustomerMembershipAsync(businessId, byPhone.Id, cancellationToken);
                return ApiResponse<User>.Ok(byPhone);
            }
        }

        // A new, unverified customer. The password is random and known to nobody:
        // the visitor never signs in with it, and can set a real one later through
        // the ordinary password-reset flow if they want an account.
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            FullName = name,
            PhoneNumber = phone,
            Role = UserRole.Customer,
            CreatedAt = DateTime.UtcNow
        };
        user.Auth = new UserAuth
        {
            Id = Guid.NewGuid(),
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(
                $"{Guid.NewGuid():N}{Guid.NewGuid():N}", workFactor: 12),
            IsVerified = false,
            CreatedAt = DateTime.UtcNow
        };

        _db.Users.Add(user);
        await EnsureCustomerMembershipAsync(businessId, user.Id, cancellationToken);
        return ApiResponse<User>.Ok(user);
    }

    /// <summary>
    /// The canonical user↔business relationship, written by its single writer so
    /// the "one membership per person per business" invariant holds under a race.
    /// </summary>
    private async Task<BusinessMembership> EnsureCustomerMembershipAsync(
        Guid businessId, Guid userId, CancellationToken cancellationToken)
    {
        var existing = await _db.BusinessMemberships
            .FirstOrDefaultAsync(m => m.UserId == userId && m.BusinessId == businessId, cancellationToken);
        if (existing != null) return existing;

        var membership = _memberships ??= new BusinessMembershipService(new UnitOfWork(_db));
        return await membership.EnsureAsync(userId, businessId, BusinessMembershipRole.Customer);
    }

    /// <summary>
    /// Queues the code through the shared notification pipeline so it commits with
    /// the record that stores its hash, and falls back to the direct email path if
    /// the pipeline cannot accept the intent — a code must never be silently lost.
    /// </summary>
    private async Task SendCodeAsync(User user, string email, string code, CancellationToken cancellationToken)
    {
        if (_notifications is null)
        {
            await _email.SendVerificationCodeAsync(email, code);
            return;
        }

        try
        {
            await _notifications.QueueAsync(new NotificationRequest(
                "security.verification_code",
                user.Id,
                null,
                new Dictionary<string, object?>
                {
                    ["code"] = code,
                    ["expiresInMinutes"] = CodeExpiryMinutes
                },
                $"guest-otp:{user.Id}:{DateTime.UtcNow:yyyyMMddHHmmssfff}",
                Force: true), cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception,
                "Guest code for {Email} could not be queued through the notification pipeline; falling back to direct email.",
                MaskEmail(email));
            await _email.SendVerificationCodeAsync(email, code);
        }
    }

    public async Task<ApiResponse<VerifyGuestBookingResponse>> VerifyAsync(
        Guid businessId, VerifyGuestBookingRequest request, CancellationToken cancellationToken = default)
    {
        if (businessId == Guid.Empty)
            return ApiResponse<VerifyGuestBookingResponse>.Fail("NOT_FOUND", "Business not found.");

        if (TenantBusinessId is Guid activeTenantId && businessId != activeTenantId)
            return ApiResponse<VerifyGuestBookingResponse>.Fail("NOT_FOUND", "Business not found.");

        var phone = NormalisePhone(request.PhoneNumber);
        if (phone == null)
            return ApiResponse<VerifyGuestBookingResponse>.Fail(
                "VALIDATION_ERROR", "Use a Kenyan phone number, for example 0712 345 678.");

        var code = (request.Code ?? string.Empty).Trim();
        if (code.Length != 6 || !code.All(char.IsAsciiDigit))
            return ApiResponse<VerifyGuestBookingResponse>.Fail(
                "VALIDATION_ERROR", "Enter the 6-digit code from the email.");

        // The business could have been downgraded between start and verify.
        if (!await _modules.IsModuleEnabledAsync(businessId, ModuleKey))
            return ApiResponse<VerifyGuestBookingResponse>.Fail(
                "MODULE_DISABLED", "This business does not take bookings through Punched.");

        var user = await _db.Users.Include(candidate => candidate.Auth)
            .FirstOrDefaultAsync(candidate => candidate.PhoneNumber == phone, cancellationToken);
        if (user?.Auth is not UserAuth auth)
            return ApiResponse<VerifyGuestBookingResponse>.Fail(
                "CODE_INVALID", "That code is not valid. Request a new one.");

        if (auth.VerificationCode is null)
            return ApiResponse<VerifyGuestBookingResponse>.Fail(
                "CODE_INVALID", "That code is not valid. Request a new one.");

        if (auth.VerificationCodeExpiresAt is null || auth.VerificationCodeExpiresAt < DateTime.UtcNow)
            return ApiResponse<VerifyGuestBookingResponse>.Fail(
                "CODE_EXPIRED", "That code has expired. Request a new one.");

        if (auth.VerificationCodeAttempts >= MaxCodeAttempts)
            return ApiResponse<VerifyGuestBookingResponse>.Fail(
                "TOO_MANY_ATTEMPTS", "Too many incorrect codes. Request a new one.");

        if (!OneTimeCode.Matches(code, auth.VerificationCode))
        {
            // Counted before answering, so guessing is bounded by the row itself
            // rather than by anything the caller controls.
            auth.VerificationCodeAttempts++;
            await _db.SaveChangesAsync(cancellationToken);
            return ApiResponse<VerifyGuestBookingResponse>.Fail(
                "CODE_INVALID", "That code is not valid. Check the email and try again.");
        }

        // Single use: the code is cleared as part of the same save that marks the
        // identity verified, so presenting it again cannot mint a second session.
        auth.IsVerified = true;
        auth.VerificationCode = null;
        auth.VerificationCodeExpiresAt = null;
        auth.VerificationCodeAttempts = 0;
        auth.LastLoginAt = DateTime.UtcNow;

        var membership = await EnsureCustomerMembershipAsync(businessId, user.Id, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);

        if (membership.Status != BusinessMembershipStatus.Active)
            return ApiResponse<VerifyGuestBookingResponse>.Fail(
                "FORBIDDEN", "This business cannot take your booking right now.");

        // The platform's ordinary short-lived customer token, scoped to this
        // business. No refresh token: the session cannot be silently extended,
        // and it expires with the booking window it exists to cover.
        var accessToken = _jwt.GenerateAccessToken(auth, user, businessId, membership.RoleName);

        _logger.LogInformation(
            "Guest identity verified for business {BusinessId}; short-lived customer token issued", businessId);

        return ApiResponse<VerifyGuestBookingResponse>.Ok(new VerifyGuestBookingResponse
        {
            AccessToken = accessToken,
            ExpiresIn = _jwtSettings.AccessTokenExpiryMinutes * 60,
            BusinessId = businessId,
            Customer = new GuestCustomerResponse
            {
                FullName = user.FullName,
                PhoneNumber = user.PhoneNumber ?? phone,
                Email = MaskEmail(user.Email)
            }
        });
    }
}
