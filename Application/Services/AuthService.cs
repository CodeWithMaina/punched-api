using System.Security.Cryptography;
using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Notifications;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;

namespace PunchedApi.Application.Services;

/// <summary>
/// Authentication service implementing registration, login, verification,
/// token refresh, logout, and profile retrieval.
/// </summary>
public class AuthService : IAuthService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly JwtTokenService _jwtService;
    private readonly IEmailService _emailService;

    /// <summary>Shared notification pipeline; null only in unit tests / legacy hosts.</summary>
    private readonly INotificationService? _notifications;
    private readonly IMapper _mapper;
    private readonly ILogger<AuthService> _logger;
    private readonly ISubscriptionProvisioningService _subscriptionProvisioning;
    private readonly IBusinessSlugGenerator _slugGenerator;
    private readonly IBusinessMembershipResolver _membershipResolver;
    private readonly ITenantHostResolver _tenantResolver;

    /// <summary>
    /// The business whose address the request came in on (null on the platform
    /// root). Used to auto-associate a signing-up customer with that business.
    /// </summary>
    private readonly ITenantContext? _tenantContext;

    // ── Constants ────────────────────────────────────────────
    private const int MaxFailedLoginAttempts = 5;
    private const int AccountLockoutMinutes = 30;
    private const int MaxVerificationAttempts = 5;
    private const int VerificationCodeExpiryMinutes = 10;
    private const int VerificationCodeLockoutMinutes = 15;
    private const int BcryptWorkFactor = 12;

    public AuthService(
        IUnitOfWork unitOfWork,
        JwtTokenService jwtService,
        IEmailService emailService,
        IMapper mapper,
        ILogger<AuthService> logger,
        ISubscriptionProvisioningService subscriptionProvisioning,
        IBusinessSlugGenerator slugGenerator,
        IBusinessMembershipResolver membershipResolver,
        ITenantHostResolver tenantResolver,
        // Optional so existing unit-test construction keeps compiling; Program.cs
        // injects the real pipeline and the OTP path falls back to direct email when
        // it is absent.
        INotificationService? notifications = null,
        // Optional for the same reason: the active tenant (page host) is only
        // available at runtime, never in unit tests.
        ITenantContext? tenantContext = null)
    {
        _unitOfWork = unitOfWork;
        _jwtService = jwtService;
        _emailService = emailService;
        _notifications = notifications;
        _mapper = mapper;
        _logger = logger;
        _subscriptionProvisioning = subscriptionProvisioning;
        _slugGenerator = slugGenerator;
        _membershipResolver = membershipResolver;
        _tenantResolver = tenantResolver;
        _tenantContext = tenantContext;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 1. Validate email not already registered (409 if exists).
    /// 2. Hash password with BCrypt (cost=12).
    /// 3. Create UserAuth + User records.
    /// 4. Generate 6-digit OTP and send via email.
    /// 5. Return success message.
    /// </remarks>
    public async Task<ApiResponse<MessageResponse>> RegisterAsync(RegisterRequest request)
    {
        try
        {
            var normalizedEmail = request.Email.Trim().ToLowerInvariant();

            // Check if email already registered
            var existingAuth = await _unitOfWork.UserAuths
                .FirstOrDefaultAsync(a => a.Email == normalizedEmail);

            if (existingAuth != null)
            {
                _logger.LogWarning("Registration attempt with existing email: {Email}", normalizedEmail);
                return ApiResponse<MessageResponse>.Fail(
                    "EMAIL_ALREADY_REGISTERED",
                    "Email already in use");
            }

            // Hash password
            var passwordHash = BCrypt.Net.BCrypt.HashPassword(request.Password, BcryptWorkFactor);

            // Generate verification code
            var verificationCode = GenerateVerificationCode();
            var verificationCodeHash = HashVerificationCode(verificationCode);
            var verificationCodeExpiresAt = DateTime.UtcNow.AddMinutes(VerificationCodeExpiryMinutes);

            // Create UserAuth
            var userAuth = new UserAuth
            {
                Id = Guid.NewGuid(),
                Email = normalizedEmail,
                PasswordHash = passwordHash,
                IsVerified = false,
                VerificationCode = verificationCodeHash,
                VerificationCodeExpiresAt = verificationCodeExpiresAt,
                VerificationCodeAttempts = 0,
                CreatedAt = DateTime.UtcNow
            };

            await _unitOfWork.UserAuths.AddAsync(userAuth);

            // Create User profile — role is ALWAYS forced server-side.
            // The public register endpoint only ever creates Customers. Business owners must use
            // /auth/register-business, and staff can only ever onboard through a valid invitation —
            // never by self-registering here.
            var role = UserRole.Customer;
            var user = new User
            {
                Id = Guid.NewGuid(),
                Email = normalizedEmail,
                FullName = request.FullName.Trim(),
                Role = role,
                SourceProvider = string.IsNullOrWhiteSpace(request.SourceProvider)
                    ? null
                    : request.SourceProvider.Trim().ToLowerInvariant(),
                SourceCampaign = string.IsNullOrWhiteSpace(request.SourceCampaign)
                    ? null
                    : request.SourceCampaign.Trim(),
                CreatedAt = DateTime.UtcNow
            };

            await _unitOfWork.Users.AddAsync(user);

            // Database-first membership: a customer who signs up on a business
            // address belongs to that business from the start. The enrollment
            // row is written in the SAME transaction as the user, so a customer
            // can never exist without the membership that grants tenant access
            // to the business they signed up on.
            var signupBusinessId = await ResolveSignupBusinessIdAsync(request.BusinessSlug);
            if (signupBusinessId is Guid businessId)
            {
                await _unitOfWork.CustomerBusinessEnrollments.AddAsync(new CustomerBusinessEnrollment
                {
                    Id = Guid.NewGuid(),
                    CustomerId = user.Id,
                    BusinessId = businessId,
                    Status = CustomerBusinessEnrollmentStatus.Active,
                    Source = "business",
                    EnrolledAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    CreatedAt = DateTime.UtcNow
                });

                _logger.LogInformation(
                    "Customer {UserId} enrolled in business {BusinessId} at registration.",
                    user.Id, businessId);
            }

            // Phase 1: the OTP intent joins this SaveChanges — the code email exists if
            // and only if the auth record storing its hash does (intent-level outbox).
            await QueueVerificationCodeSafelyAsync(user.Id, normalizedEmail, verificationCode, verificationCodeExpiresAt);

            await _unitOfWork.SaveChangesAsync();

            _logger.LogInformation("User registered successfully: {Email}", normalizedEmail);

            return ApiResponse<MessageResponse>.Ok(new MessageResponse
            {
                Message = "Registration successful. Check your email for verification code."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during registration for email: {Email}", request.Email);
            return ApiResponse<MessageResponse>.Fail(
                "REGISTRATION_FAILED",
                "An unexpected error occurred during registration.");
        }
    }

/// <inheritdoc />
    /// <remarks>
    /// Atomic business onboarding: creates UserAuth, Business-role User, and the Business in a single
    /// transaction (one SaveChanges), then sends a verification code. If any insert fails the whole
    /// operation rolls back so no orphan account or business can exist.
    /// </remarks>
    public async Task<ApiResponse<RegisterBusinessResponse>> RegisterBusinessAsync(RegisterBusinessRequest request)
    {
        try
        {
            var normalizedEmail = request.Email.Trim().ToLowerInvariant();

            // 1. Email must be free.
            var existingAuth = await _unitOfWork.UserAuths.FirstOrDefaultAsync(a => a.Email == normalizedEmail);
            if (existingAuth != null)
            {
                _logger.LogWarning("Business registration attempt with existing email: {Email}", normalizedEmail);
                return ApiResponse<RegisterBusinessResponse>.Fail("EMAIL_ALREADY_REGISTERED", "Email already in use");
            }

            // 2. Duplicate business name guard (soft, case-insensitive) among active businesses only.
            var duplicateName = await _unitOfWork.Businesses.AnyAsync(b =>
                b.Name.ToLower() == request.BusinessName.Trim().ToLowerInvariant());
            if (duplicateName)
            {
                return ApiResponse<RegisterBusinessResponse>.Fail("BUSINESS_NAME_TAKEN", "A business with this name already exists.");
            }

            var passwordHash = BCrypt.Net.BCrypt.HashPassword(request.Password, BcryptWorkFactor);
            var verificationCode = GenerateVerificationCode();
            var verificationCodeHash = HashVerificationCode(verificationCode);
            var verificationCodeExpiresAt = DateTime.UtcNow.AddMinutes(VerificationCodeExpiryMinutes);

            var userAuth = new UserAuth
            {
                Id = Guid.NewGuid(),
                Email = normalizedEmail,
                PasswordHash = passwordHash,
                IsVerified = false,
                VerificationCode = verificationCodeHash,
                VerificationCodeExpiresAt = verificationCodeExpiresAt,
                VerificationCodeAttempts = 0,
                CreatedAt = DateTime.UtcNow
            };
            await _unitOfWork.UserAuths.AddAsync(userAuth);

            var user = new User
            {
                Id = Guid.NewGuid(),
                Email = normalizedEmail,
                FullName = request.FullName.Trim(),
                PhoneNumber = request.PhoneNumber?.Trim(),
                Role = UserRole.Business,
                CreatedAt = DateTime.UtcNow
            };
            await _unitOfWork.Users.AddAsync(user);

            var business = new Business
            {
                Id = Guid.NewGuid(),
                Name = request.BusinessName.Trim(),
                Category = request.BusinessCategory.Trim(),
                Location = request.BusinessLocation.Trim(),
                PhoneNumber = request.BusinessPhone?.Trim(),
                Email = request.BusinessEmail?.Trim().ToLowerInvariant(),
                Description = request.BusinessDescription?.Trim(),
                LogoUrl = request.LogoUrl?.Trim(),
                MpesaNumber = request.BusinessMpesaNumber.Trim(),
                // Subdomain address (java-house.punched.app) — generated,
                // collision-checked slug; see IBusinessSlugGenerator.
                Slug = await _slugGenerator.GenerateAsync(request.BusinessName),
                OwnerId = user.Id,
                CreatedAt = DateTime.UtcNow
            };
            await _unitOfWork.Businesses.AddAsync(business);

            // Phase 1: the OTP intent joins the atomic commit of auth + user + business
            // (intent-level outbox) — no code email can reference an account that never
            // exists, and no committed account can exist without its code intent.
            await QueueVerificationCodeSafelyAsync(user.Id, normalizedEmail, verificationCode, verificationCodeExpiresAt);

            try
            {
                // Atomic commit — all three records are persisted or none are.
                await _unitOfWork.SaveChangesAsync();
            }
            catch (DbUpdateException) when (business.Slug != null)
            {
                // Lost a unique-index race (two same-named signups at once) —
                // retry once with a disambiguated slug. A second failure
                // escapes to the outer catch as REGISTRATION_FAILED.
                business.Slug = BusinessSlugPolicy.Fallback(BusinessSlugPolicy.Slugify(request.BusinessName));
                await _unitOfWork.SaveChangesAsync();
            }

            // Give the new business immediate module access (default Starter plan)
            // so it is not locked out. Best-effort: no-op if the Starter plan is
            // not yet seeded or provisioning fails.
            await _subscriptionProvisioning.EnsureDefaultSubscriptionAsync(business.Id);

            _logger.LogInformation("Business registered successfully: {Email} / {Business}", normalizedEmail, business.Name);

            return ApiResponse<RegisterBusinessResponse>.Ok(new RegisterBusinessResponse
            {
                Message = "Business account created. Check your email for the verification code.",
                Business = new BusinessResponse
                {
                    Id = business.Id,
                    Slug = business.Slug,
                    Name = business.Name,
                    Category = business.Category,
                    Location = business.Location,
                    PhoneNumber = business.PhoneNumber,
                    Email = business.Email,
                    Description = business.Description,
                    LogoUrl = business.LogoUrl,
                    OwnerId = business.OwnerId
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during business registration for email: {Email}", request.Email);
            return ApiResponse<RegisterBusinessResponse>.Fail(
                "REGISTRATION_FAILED",
                "An unexpected error occurred during registration.");
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// 1. Find UserAuth by email (404 if not found).
    /// 2. Check verification code not expired (410 if expired).
    /// 3. Compare code hash (401 if wrong, track attempts).
    /// 4. Set IsVerified = true.
    /// 5. Generate JWT tokens and return.
    /// </remarks>
    public async Task<ApiResponse<AuthResponse>> VerifyEmailAsync(VerifyEmailRequest request)
    {
        try
        {
            var normalizedEmail = request.Email.Trim().ToLowerInvariant();

            var userAuth = await _unitOfWork.UserAuths
                .FirstOrDefaultAsync(a => a.Email == normalizedEmail);

            if (userAuth == null)
            {
                return ApiResponse<AuthResponse>.Fail(
                    "USER_NOT_FOUND",
                    "No account found with this email.");
            }

            if (userAuth.IsVerified)
            {
                return ApiResponse<AuthResponse>.Fail(
                    "ALREADY_VERIFIED",
                    "Email is already verified.");
            }

            // Check if verification code is expired
            if (userAuth.VerificationCodeExpiresAt == null ||
                userAuth.VerificationCodeExpiresAt < DateTime.UtcNow)
            {
                return ApiResponse<AuthResponse>.Fail(
                    "CODE_EXPIRED",
                    "Verification code has expired. Request a new one.");
            }

            // Check max attempts
            if (userAuth.VerificationCodeAttempts >= MaxVerificationAttempts)
            {
                return ApiResponse<AuthResponse>.Fail(
                    "TOO_MANY_ATTEMPTS",
                    $"Too many wrong attempts. Try again in {VerificationCodeLockoutMinutes} minutes.");
            }

            // Verify code
            var codeHash = HashVerificationCode(request.Code);
            if (userAuth.VerificationCode != codeHash)
            {
                userAuth.VerificationCodeAttempts++;
                _unitOfWork.UserAuths.Update(userAuth);
                await _unitOfWork.SaveChangesAsync();

                var remaining = MaxVerificationAttempts - userAuth.VerificationCodeAttempts;
                return ApiResponse<AuthResponse>.Fail(
                    "INVALID_VERIFICATION_CODE",
                    $"Wrong verification code. {remaining} attempts remaining.");
            }

            // Mark as verified
            userAuth.IsVerified = true;
            userAuth.VerificationCode = null;
            userAuth.VerificationCodeExpiresAt = null;
            userAuth.VerificationCodeAttempts = 0;
            userAuth.LastLoginAt = DateTime.UtcNow;
            _unitOfWork.UserAuths.Update(userAuth);

            // Get user profile
            var user = await _unitOfWork.Users
                .FirstOrDefaultAsync(u => u.Email == normalizedEmail);

            if (user == null)
            {
                return ApiResponse<AuthResponse>.Fail(
                    "PROFILE_NOT_FOUND",
                    "User profile not found.");
            }

                    if (user.IsDeleted)
                    {
                    return ApiResponse<AuthResponse>.Fail(
                        "ACCOUNT_DISABLED",
                        "This account has been disabled.");
                    }

            // Generate tokens. Tenant claims are minted only when the caller's
            // membership of the tenant host is verified server-side.
            //
            // Backstop: the enrollment written at registration may predate the
            // business (rare) or have been created by a different flow. Making
            // sure the membership row exists BEFORE the tenant context is
            // resolved guarantees the customer is associated with the business
            // they signed up on — and therefore receives its tenant claims.
            var verificationBusinessId = await ResolveSignupBusinessIdAsync(request.BusinessSlug);
            if (verificationBusinessId is Guid verificationBiz)
            {
                try
                {
                    await EnsureBusinessEnrollmentAsync(user.Id, verificationBiz, "business");
                    await _unitOfWork.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    // Never fail a verification over the association row.
                    _logger.LogWarning(ex,
                        "Could not associate customer {UserId} with business {BusinessId}.",
                        user.Id, verificationBiz);
                }
            }

            var tenant = await ResolveTenantContextAsync(user, request.BusinessSlug);
            var accessToken = _jwtService.GenerateAccessToken(userAuth, user, tenant?.BusinessId, tenant?.BizRole);
            var refreshTokenValue = _jwtService.GenerateRefreshToken();

            // Store refresh token
            var refreshToken = new RefreshToken
            {
                Id = Guid.NewGuid(),
                UserAuthId = userAuth.Id,
                Token = refreshTokenValue,
                ExpiresAt = _jwtService.RefreshTokenExpiry,
                CreatedAt = DateTime.UtcNow
            };

            await _unitOfWork.RefreshTokens.AddAsync(refreshToken);
            await _unitOfWork.SaveChangesAsync();

            _logger.LogInformation("Email verified successfully: {Email}", normalizedEmail);

            // Fire-and-forget welcome email
            _ = Task.Run(async () =>
            {
                try { await _emailService.SendWelcomeAsync(normalizedEmail, user.FullName); }
                catch (Exception ex) { _logger.LogWarning(ex, "Non-critical: failed to send welcome email to {Email}", normalizedEmail); }
            });

            return ApiResponse<AuthResponse>.Ok(new AuthResponse
            {
                AccessToken = accessToken,
                RefreshToken = refreshTokenValue,
                ExpiresIn = _jwtService.AccessTokenExpirySeconds,
                User = _mapper.Map<UserProfileResponse>(user)
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during email verification: {Email}", request.Email);
            return ApiResponse<AuthResponse>.Fail(
                "VERIFICATION_FAILED",
                "An unexpected error occurred during verification.");
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// 1. Find UserAuth by email.
    /// 2. Check account not locked.
    /// 3. Check email verified.
    /// 4. Verify password hash.
    /// 5. Reset failed attempts, generate tokens.
    /// </remarks>
    public async Task<ApiResponse<AuthResponse>> LoginAsync(LoginRequest request)
    {
        try
        {
            var normalizedEmail = request.Email.Trim().ToLowerInvariant();

            var userAuth = await _unitOfWork.UserAuths
                .FirstOrDefaultAsync(a => a.Email == normalizedEmail);

            if (userAuth == null)
            {
                return ApiResponse<AuthResponse>.Fail(
                    "INVALID_CREDENTIALS",
                    "Invalid email or password.");
            }

            // Check if account is locked
            if (userAuth.LockedUntil.HasValue && userAuth.LockedUntil > DateTime.UtcNow)
            {
                var minutesRemaining = (int)(userAuth.LockedUntil.Value - DateTime.UtcNow).TotalMinutes + 1;
                return ApiResponse<AuthResponse>.Fail(
                    "ACCOUNT_LOCKED",
                    $"Account locked. Try again in {minutesRemaining} minutes.");
            }

            // Check if email is verified
            if (!userAuth.IsVerified)
            {
                return ApiResponse<AuthResponse>.Fail(
                    "ACCOUNT_NOT_VERIFIED",
                    "Please verify your email before logging in.");
            }

            // Verify password
            if (!BCrypt.Net.BCrypt.Verify(request.Password, userAuth.PasswordHash))
            {
                userAuth.FailedLoginAttempts++;

                if (userAuth.FailedLoginAttempts >= MaxFailedLoginAttempts)
                {
                    userAuth.LockedUntil = DateTime.UtcNow.AddMinutes(AccountLockoutMinutes);
                    _logger.LogWarning("Account locked due to too many failed attempts: {Email}", normalizedEmail);
                }

                _unitOfWork.UserAuths.Update(userAuth);
                await _unitOfWork.SaveChangesAsync();

                return ApiResponse<AuthResponse>.Fail(
                    "INVALID_CREDENTIALS",
                    "Invalid email or password.");
            }

            // Successful login - reset failed attempts
            userAuth.FailedLoginAttempts = 0;
            userAuth.LockedUntil = null;
            userAuth.LastLoginAt = DateTime.UtcNow;
            _unitOfWork.UserAuths.Update(userAuth);

            // Get user profile
            var user = await _unitOfWork.Users
                .FirstOrDefaultAsync(u => u.Email == normalizedEmail);

            if (user == null)
            {
                return ApiResponse<AuthResponse>.Fail(
                    "PROFILE_NOT_FOUND",
                    "User profile not found.");
            }

                    if (user.IsDeleted)
                    {
                    return ApiResponse<AuthResponse>.Fail(
                        "ACCOUNT_DISABLED",
                        "This account has been disabled.");
                    }

            // Generate tokens. Tenant-aware: signing in on java-house.punched.app
            // issues biz/bizRole claims once membership is verified.
            var tenant = await ResolveTenantContextAsync(user, request.BusinessSlug, request.BusinessId);
            var accessToken = _jwtService.GenerateAccessToken(userAuth, user, tenant?.BusinessId, tenant?.BizRole);
            var refreshTokenValue = _jwtService.GenerateRefreshToken();

            var refreshToken = new RefreshToken
            {
                Id = Guid.NewGuid(),
                UserAuthId = userAuth.Id,
                Token = refreshTokenValue,
                ExpiresAt = _jwtService.RefreshTokenExpiry,
                CreatedAt = DateTime.UtcNow
            };

            await _unitOfWork.RefreshTokens.AddAsync(refreshToken);
            await _unitOfWork.SaveChangesAsync();

            _logger.LogInformation("User logged in successfully: {Email}", normalizedEmail);

            return ApiResponse<AuthResponse>.Ok(new AuthResponse
            {
                AccessToken = accessToken,
                RefreshToken = refreshTokenValue,
                ExpiresIn = _jwtService.AccessTokenExpirySeconds,
                User = _mapper.Map<UserProfileResponse>(user)
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during login: {Email}", request.Email);
            return ApiResponse<AuthResponse>.Fail(
                "LOGIN_FAILED",
                "An unexpected error occurred during login.");
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// 1. Find refresh token in database.
    /// 2. Validate not expired or revoked.
    /// 3. Revoke old token, issue new pair.
    /// </remarks>
    public async Task<ApiResponse<TokenResponse>> RefreshTokenAsync(string refreshTokenValue, string? businessSlug = null)
    {
        try
        {
            var storedToken = await _unitOfWork.RefreshTokens
                .FirstOrDefaultAsync(t => t.Token == refreshTokenValue && !t.IsRevoked);

            if (storedToken == null)
            {
                return ApiResponse<TokenResponse>.Fail(
                    "INVALID_REFRESH_TOKEN",
                    "Invalid or expired refresh token.");
            }

            if (storedToken.ExpiresAt < DateTime.UtcNow)
            {
                storedToken.IsRevoked = true;
                storedToken.RevokedAt = DateTime.UtcNow;
                _unitOfWork.RefreshTokens.Update(storedToken);
                await _unitOfWork.SaveChangesAsync();

                return ApiResponse<TokenResponse>.Fail(
                    "REFRESH_TOKEN_EXPIRED",
                    "Refresh token has expired. Please login again.");
            }

            // Get UserAuth and User
            var userAuth = await _unitOfWork.UserAuths.GetByIdAsync(storedToken.UserAuthId);
            if (userAuth == null)
            {
                return ApiResponse<TokenResponse>.Fail(
                    "USER_NOT_FOUND",
                    "User not found.");
            }

            var user = await _unitOfWork.Users
                .FirstOrDefaultAsync(u => u.Email == userAuth.Email);

            if (user == null)
            {
                return ApiResponse<TokenResponse>.Fail(
                    "PROFILE_NOT_FOUND",
                    "User profile not found.");
            }

            if (user.IsDeleted)
            {
                return ApiResponse<TokenResponse>.Fail(
                    "ACCOUNT_DISABLED",
                    "This account has been disabled.");
            }

            // Revoke old token
            storedToken.IsRevoked = true;
            storedToken.RevokedAt = DateTime.UtcNow;
            _unitOfWork.RefreshTokens.Update(storedToken);

            // Generate new tokens. Tenant context is RECONSTRUCTED from the
            // requesting origin and re-verified against live membership, so a
            // refresh can neither drop nor forge biz/bizRole.
            var tenant = await ResolveTenantContextAsync(user, businessSlug);
            var newAccessToken = _jwtService.GenerateAccessToken(userAuth, user, tenant?.BusinessId, tenant?.BizRole);
            var newRefreshTokenValue = _jwtService.GenerateRefreshToken();

            var newRefreshToken = new RefreshToken
            {
                Id = Guid.NewGuid(),
                UserAuthId = userAuth.Id,
                Token = newRefreshTokenValue,
                ExpiresAt = _jwtService.RefreshTokenExpiry,
                CreatedAt = DateTime.UtcNow
            };

            await _unitOfWork.RefreshTokens.AddAsync(newRefreshToken);
            await _unitOfWork.SaveChangesAsync();

            return ApiResponse<TokenResponse>.Ok(new TokenResponse
            {
                AccessToken = newAccessToken,
                RefreshToken = newRefreshTokenValue,
                ExpiresIn = _jwtService.AccessTokenExpirySeconds
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during token refresh");
            return ApiResponse<TokenResponse>.Fail(
                "TOKEN_REFRESH_FAILED",
                "An unexpected error occurred during token refresh.");
        }
    }

    /// <summary>
    /// Resolves the optional tenant context supplied at an auth entry point.
    ///
    /// The client sends a HINT (the tenant subdomain, or an explicit business
    /// id); this method resolves it to a business and verifies the caller's
    /// membership against the authoritative server-side relationships through
    /// <see cref="IBusinessMembershipResolver"/> (Owner → <c>Business.OwnerId</c>,
    /// Staff → <c>User.StaffBusinessId</c>, Customer → active enrollment).
    ///
    /// Returns null whenever the tenant cannot be verified: authentication
    /// itself never fails because of tenant context — the token is simply
    /// minted without tenant claims, exactly like a legacy token.
    /// </summary>
    private async Task<(Guid BusinessId, string BizRole)?> ResolveTenantContextAsync(
        User user,
        string? businessSlug,
        Guid? explicitBusinessId = null)
    {
        try
        {
            var businessId = explicitBusinessId;

            if (businessId is null && !string.IsNullOrWhiteSpace(businessSlug))
                businessId = await _tenantResolver.ResolveBusinessIdAsync(businessSlug);

            // Fall back to the ACTIVE TENANT of this request (the business
            // address the page is on). The slug hint is optional in the body —
            // the host is the authoritative signal — and the membership check
            // below still applies, so this can never grant access.
            if (businessId is null && _tenantContext?.IsActive == true)
                businessId = _tenantContext.BusinessId;

            if (businessId is null || businessId == Guid.Empty)
                return null;

            var membership = await _membershipResolver.ResolveAsync(user.Id, businessId.Value);
            if (!membership.IsActive || string.IsNullOrWhiteSpace(membership.BizRole))
                return null;

            return (businessId.Value, membership.BizRole!);
        }
        catch (Exception ex)
        {
            // Tenant enrichment is best-effort: a resolution failure must never
            // block an otherwise valid sign-in or refresh.
            _logger.LogWarning(ex, "Tenant context resolution failed for user {UserId}", user.Id);
            return null;
        }
    }

    /// <summary>
    /// Resolves the business a signing-up customer should be associated with.
    ///
    /// <para>Order of precedence:</para>
    /// <list type="number">
    /// <item>The <b>active tenant</b> of the request (the business subdomain the
    /// sign-up was entered on) — the authoritative answer.</item>
    /// <item>The <c>businessSlug</c> HINT in the body, resolved server-side.</item>
    /// </list>
    ///
    /// <para>Both are only ever <b>narrowing</b> inputs: the returned id is a
    /// real, non-deleted business resolved from the server's own data, never a
    /// client-supplied identifier. Returns null when nothing resolves (platform
    /// root) — the customer is then simply not enrolled in any business.</para>
    /// </summary>
    private async Task<Guid?> ResolveSignupBusinessIdAsync(string? businessSlug)
    {
        try
        {
            // 1. The address the request was made on wins: it is the surface
            //    the customer was actually looking at when they signed up.
            if (_tenantContext?.IsActive == true && _tenantContext.BusinessId is Guid tenantBusinessId)
            {
                var tenantBusinessExists = await _unitOfWork.Businesses
                    .AnyAsync(b => b.Id == tenantBusinessId && !b.IsDeleted);
                if (tenantBusinessExists)
                    return tenantBusinessId;
            }

            // 2. Fall back to the client-supplied slug hint (resolved, never trusted).
            if (string.IsNullOrWhiteSpace(businessSlug))
                return null;

            var hinted = await _tenantResolver.ResolveBusinessIdAsync(businessSlug.Trim());
            if (hinted is null || hinted == Guid.Empty)
                return null;

            var hintedExists = await _unitOfWork.Businesses
                .AnyAsync(b => b.Id == hinted.Value && !b.IsDeleted);
            return hintedExists ? hinted : null;
        }
        catch (Exception ex)
        {
            // Association is a convenience, never a blocker: a failure here
            // must not prevent the account from being created.
            _logger.LogWarning(ex, "Could not resolve the business for this registration.");
            return null;
        }
    }

    /// <summary>
    /// Ensures the customer has an enrollment row in <paramref name="businessId"/>.
    /// Idempotent: an EXISTING row is left exactly as it is (a customer who
    /// deliberately left or was blocked is never silently re-added), and rows
    /// belonging to ANOTHER business are never touched.
    /// </summary>
    private async Task EnsureBusinessEnrollmentAsync(Guid customerId, Guid businessId, string source)
    {
        var existing = await _unitOfWork.CustomerBusinessEnrollments
            .FirstOrDefaultAsync(e => e.CustomerId == customerId && e.BusinessId == businessId);

        if (existing != null)
            return;

        await _unitOfWork.CustomerBusinessEnrollments.AddAsync(new CustomerBusinessEnrollment
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            BusinessId = businessId,
            Status = CustomerBusinessEnrollmentStatus.Active,
            Source = source,
            EnrolledAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        });
    }

    /// <inheritdoc />
    /// <remarks>
    /// Revokes all active refresh tokens for the given user.
    /// </remarks>
    public async Task<ApiResponse<MessageResponse>> LogoutAsync(Guid userAuthId)
    {
        try
        {
            var tokens = await _unitOfWork.RefreshTokens
                .FindAsync(t => t.UserAuthId == userAuthId && !t.IsRevoked);

            foreach (var token in tokens)
            {
                token.IsRevoked = true;
                token.RevokedAt = DateTime.UtcNow;
                _unitOfWork.RefreshTokens.Update(token);
            }

            await _unitOfWork.SaveChangesAsync();

            _logger.LogInformation("User logged out, all tokens revoked: {UserAuthId}", userAuthId);

            return ApiResponse<MessageResponse>.Ok(new MessageResponse
            {
                Message = "Logged out successfully"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during logout: {UserAuthId}", userAuthId);
            return ApiResponse<MessageResponse>.Fail(
                "LOGOUT_FAILED",
                "An unexpected error occurred during logout.");
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Generates a new 6-digit code and sends to the email.
    /// Rate limited: max 3 per email per 15 minutes (checked at middleware level).
    /// </remarks>
    public async Task<ApiResponse<MessageResponse>> RequestVerificationCodeAsync(RequestEmailRequest request)
    {
        try
        {
            var normalizedEmail = request.Email.Trim().ToLowerInvariant();

            var userAuth = await _unitOfWork.UserAuths
                .FirstOrDefaultAsync(a => a.Email == normalizedEmail);

            if (userAuth == null)
            {
                // Don't reveal if email exists — return success regardless
                return ApiResponse<MessageResponse>.Ok(new MessageResponse
                {
                    Message = $"If the email is registered, a verification code has been sent to {normalizedEmail}. Valid for 10 minutes."
                });
            }

            // Generate new code
            var verificationCode = GenerateVerificationCode();
            var verificationCodeHash = HashVerificationCode(verificationCode);
            var verificationCodeExpiresAt = DateTime.UtcNow.AddMinutes(VerificationCodeExpiryMinutes);

            userAuth.VerificationCode = verificationCodeHash;
            userAuth.VerificationCodeExpiresAt = verificationCodeExpiresAt;
            userAuth.VerificationCodeAttempts = 0;
            _unitOfWork.UserAuths.Update(userAuth);

            // Phase 1: the OTP intent joins this SaveChanges (intent-level outbox).
            var recipient = await _unitOfWork.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail);
            if (recipient != null)
                await QueueVerificationCodeSafelyAsync(recipient.Id, normalizedEmail, verificationCode, verificationCodeExpiresAt);
            else
                await _emailService.SendVerificationCodeAsync(normalizedEmail, verificationCode);

            await _unitOfWork.SaveChangesAsync();

            _logger.LogInformation("Verification code sent to: {Email}", normalizedEmail);

            return ApiResponse<MessageResponse>.Ok(new MessageResponse
            {
                Message = $"Verification code sent to {normalizedEmail}. Valid for 10 minutes."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error requesting verification code: {Email}", request.Email);
            return ApiResponse<MessageResponse>.Fail(
                "REQUEST_CODE_FAILED",
                "An unexpected error occurred.");
        }
    }

    /// <inheritdoc />
    public async Task<ApiResponse<UserProfileResponse>> GetProfileAsync(Guid userAuthId)
    {
        try
        {
            // Read-only, projected: we only need the auth email to locate the
            // profile row. AsNoTracking + single-column projection avoids
            // loading (and change-tracking) the full UserAuth entity — password
            // hash, refresh tokens, lockout state — on this hot path.
            var authEmails = await _unitOfWork.UserAuths
                .SelectNoTrackingAsync(a => a.Id == userAuthId, a => a.Email);
            var authEmail = authEmails.Count > 0 ? authEmails[0] : null;
            if (string.IsNullOrEmpty(authEmail))
            {
                return ApiResponse<UserProfileResponse>.Fail(
                    "USER_NOT_FOUND",
                    "User not found.");
            }

            // Read-only profile projection: no change tracking, only the columns
            // the mapper needs. The two lookups are inherently sequential (the
            // User row is found by the UserAuth email), so no parallelization.
            var user = await _unitOfWork.Users
                .FirstOrDefaultNoTrackingAsync(u => u.Email == authEmail);

            if (user == null)
            {
                return ApiResponse<UserProfileResponse>.Fail(
                    "PROFILE_NOT_FOUND",
                    "User profile not found.");
            }

            return ApiResponse<UserProfileResponse>.Ok(
                _mapper.Map<UserProfileResponse>(user));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting profile: {UserAuthId}", userAuthId);
            return ApiResponse<UserProfileResponse>.Fail(
                "PROFILE_FETCH_FAILED",
                "An unexpected error occurred.");
        }
    }

    // ── Private Helpers ─────────────────────────────────────

    /// <summary>
    /// Queues the OTP through the shared notification pipeline BEFORE the caller's
    /// SaveChanges so the code email commits atomically with the auth record that stores
    /// its hash (Phase 1 intent-level outbox; SECURITY category bypasses preferences —
    /// R2 — and the worker applies the urgent retry profile from the type registry).
    /// Falls back to the direct email path only when the pipeline cannot accept the
    /// intent: an OTP must never be silently lost.
    /// </summary>
    private async Task QueueVerificationCodeSafelyAsync(
        Guid userId, string email, string verificationCode, DateTime expiresAtUtc)
    {
        if (_notifications is null)
        {
            await _emailService.SendVerificationCodeAsync(email, verificationCode);
            return;
        }

        try
        {
            await _notifications.QueueAsync(new NotificationRequest(
                "security.verification_code",
                userId,
                null,
                new Dictionary<string, object?>
                {
                    ["code"] = verificationCode,
                    ["expiresInMinutes"] = VerificationCodeExpiryMinutes
                },
                $"otp:{userId}:{expiresAtUtc:yyyyMMddHHmmssfff}",
                Force: true));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Verification code for {Email} could not be queued through the notification pipeline; falling back to direct email.",
                email);
            await _emailService.SendVerificationCodeAsync(email, verificationCode);
        }
    }

    /// <inheritdoc />
    public async Task<ApiResponse<MessageResponse>> ForgotPasswordAsync(ForgotPasswordRequest request)
    {
        try
        {
            var normalizedEmail = request.Email.Trim().ToLowerInvariant();

            var userAuth = await _unitOfWork.UserAuths
                .FirstOrDefaultAsync(a => a.Email == normalizedEmail);

            if (userAuth == null)
            {
                // Don't reveal if email exists
                return ApiResponse<MessageResponse>.Ok(new MessageResponse
                {
                    Message = $"If the email is registered, a password reset code has been sent to {normalizedEmail}."
                });
            }

            // Generate and store code
            var code = GenerateVerificationCode();
            var codeHash = HashVerificationCode(code);

            userAuth.VerificationCode = codeHash;
            userAuth.VerificationCodeExpiresAt = DateTime.UtcNow.AddMinutes(VerificationCodeExpiryMinutes);
            userAuth.VerificationCodeAttempts = 0;
            _unitOfWork.UserAuths.Update(userAuth);
            await _unitOfWork.SaveChangesAsync();

            await _emailService.SendPasswordResetCodeAsync(normalizedEmail, code);

            _logger.LogInformation("Password reset code sent to: {Email}", normalizedEmail);

            return ApiResponse<MessageResponse>.Ok(new MessageResponse
            {
                Message = $"If the email is registered, a password reset code has been sent to {normalizedEmail}."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during forgot password: {Email}", request.Email);
            return ApiResponse<MessageResponse>.Fail("FORGOT_PASSWORD_FAILED", "An unexpected error occurred.");
        }
    }

    /// <inheritdoc />
    public async Task<ApiResponse<MessageResponse>> ResetPasswordAsync(ResetPasswordRequest request)
    {
        try
        {
            var normalizedEmail = request.Email.Trim().ToLowerInvariant();

            var userAuth = await _unitOfWork.UserAuths
                .FirstOrDefaultAsync(a => a.Email == normalizedEmail);

            if (userAuth == null)
            {
                return ApiResponse<MessageResponse>.Fail("INVALID_REQUEST", "Invalid email or code.");
            }

            // Check code expiry
            if (userAuth.VerificationCodeExpiresAt == null ||
                userAuth.VerificationCodeExpiresAt < DateTime.UtcNow)
            {
                return ApiResponse<MessageResponse>.Fail("CODE_EXPIRED", "Reset code has expired. Request a new one.");
            }

            // Check max attempts
            if (userAuth.VerificationCodeAttempts >= MaxVerificationAttempts)
            {
                return ApiResponse<MessageResponse>.Fail(
                    "TOO_MANY_ATTEMPTS",
                    $"Too many wrong attempts. Request a new code.");
            }

            // Verify code
            var codeHash = HashVerificationCode(request.Code);
            if (userAuth.VerificationCode != codeHash)
            {
                userAuth.VerificationCodeAttempts++;
                _unitOfWork.UserAuths.Update(userAuth);
                await _unitOfWork.SaveChangesAsync();

                var remaining = MaxVerificationAttempts - userAuth.VerificationCodeAttempts;
                return ApiResponse<MessageResponse>.Fail(
                    "INVALID_CODE",
                    $"Wrong code. {remaining} attempts remaining.");
            }

            // Update password
            userAuth.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword, BcryptWorkFactor);
            userAuth.VerificationCode = null;
            userAuth.VerificationCodeExpiresAt = null;
            userAuth.VerificationCodeAttempts = 0;
            userAuth.FailedLoginAttempts = 0;
            userAuth.LockedUntil = null;
            _unitOfWork.UserAuths.Update(userAuth);
            await _unitOfWork.SaveChangesAsync();

            _logger.LogInformation("Password reset successfully: {Email}", normalizedEmail);

            return ApiResponse<MessageResponse>.Ok(new MessageResponse
            {
                Message = "Password has been reset successfully. You can now log in."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during password reset: {Email}", request.Email);
            return ApiResponse<MessageResponse>.Fail("RESET_FAILED", "An unexpected error occurred.");
        }
    }

    /// <summary>
    /// Generates a cryptographically secure 6-digit verification code.
    /// </summary>
    private static string GenerateVerificationCode()
    {
        using var rng = RandomNumberGenerator.Create();
        var bytes = new byte[4];
        rng.GetBytes(bytes);
        var code = Math.Abs(BitConverter.ToInt32(bytes, 0)) % 1_000_000;
        return code.ToString("D6");
    }

    /// <summary>
    /// Hashes a verification code with SHA256 for secure storage.
    /// </summary>
    private static string HashVerificationCode(string code)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(code);
        var hash = sha256.ComputeHash(bytes);
        return Convert.ToBase64String(hash);
    }

    /// <inheritdoc />
    public async Task<ApiResponse<MessageResponse>> ChangePasswordAsync(Guid userAuthId, ChangePasswordRequest request)
    {
        var userAuth = await _unitOfWork.UserAuths
            .FirstOrDefaultAsync(ua => ua.Id == userAuthId);

        if (userAuth == null)
            return ApiResponse<MessageResponse>.Fail("NOT_FOUND", "User not found.");

        var user = await _unitOfWork.Users.FirstOrDefaultAsync(u => u.Email == userAuth.Email);
        if (user == null || user.IsDeleted)
            return ApiResponse<MessageResponse>.Fail("ACCOUNT_DISABLED", "This account has been disabled.");

        // Verify current password
        if (!BCrypt.Net.BCrypt.Verify(request.CurrentPassword, userAuth.PasswordHash))
            return ApiResponse<MessageResponse>.Fail("INVALID_PASSWORD", "Current password is incorrect.");

        // Ensure new password differs
        if (BCrypt.Net.BCrypt.Verify(request.NewPassword, userAuth.PasswordHash))
            return ApiResponse<MessageResponse>.Fail("SAME_PASSWORD", "New password must be different from current password.");

        // Update password
        userAuth.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword, BcryptWorkFactor);
        _unitOfWork.UserAuths.Update(userAuth);
        await _unitOfWork.SaveChangesAsync();

        _logger.LogInformation("Password changed for user {UserAuthId}", userAuthId);

        return ApiResponse<MessageResponse>.Ok(new MessageResponse
        {
            Message = "Password changed successfully."
        });
    }
}
