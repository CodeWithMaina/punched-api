using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Mappings;
using PunchedApi.Application.Services;
using PunchedApi.Application.Settings;
using PunchedApi.Application.Validators;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;
using PunchedApi.Infrastructure.Repositories;

namespace PunchedApi.Tests;

/// <summary>
/// Locks the canonical identity/persona model described in
/// <c>PunchedApi/docs/auth-architecture.md</c>:
///
///  • public registration can only create a Customer;
///  • POST /v1/businesses (BusinessService.CreateBusinessAsync) is the only way to
///    become an owner, and it promotes + inserts atomically;
///  • staff is only ever granted by an owner-issued invitation, and the
///    existing-account path requires the session's email to match the invited one;
///  • every staff acceptance issues tenant-aware tokens (biz + bizRole).
///
/// These are the invariants; endpoint wiring and HTTP status mapping live in the
/// controllers and are covered by AuthorizationPostureTests.
/// </summary>
public class AuthModelInvariantsTests
{
    private const string TestToken = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string InvitedEmail = "staff@example.com";
    private const string ValidPassword = "P@ssw0rd!12";

    private static ApplicationDbContext CreateContext(string name) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(name)
            .Options);

    private static JwtTokenService CreateJwtService() =>
        new(Options.Create(new JwtSettings
        {
            Secret = "this-is-a-test-secret-that-is-at-least-32-characters-long",
            Issuer = "PunchedApi-Test",
            Audience = "PunchedApi-Tests",
            AccessTokenExpiryMinutes = 60,
            RefreshTokenExpiryDays = 30
        }));

    private static IMapper CreateMapper() =>
        new MapperConfiguration(cfg => cfg.AddProfile<MappingProfile>()).CreateMapper();

    private static AuthService CreateAuthService(ApplicationDbContext context, Guid? tenantBusinessId = null)
    {
        var emailMock = new Mock<IEmailService>();
        emailMock.Setup(e => e.SendVerificationCodeAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        // Host/slug resolution is a server-side verification step; tests pin it
        // so the tenant claim path can be asserted without a real host header.
        var tenantResolver = new Mock<ITenantHostResolver>();
        tenantResolver.Setup(r => r.ResolveBusinessIdAsync(It.IsAny<string>()))
            .ReturnsAsync(tenantBusinessId);

        return new AuthService(
            new UnitOfWork(context),
            CreateJwtService(),
            emailMock.Object,
            CreateMapper(),
            TestHelpers.CreateLogger<AuthService>(),
            new SubscriptionProvisioningService(context, TestHelpers.CreateLogger<SubscriptionProvisioningService>()),
            new BusinessSlugGenerator(context),
            new BusinessMembershipResolver(context),
            tenantResolver.Object);
    }

    private static BusinessService CreateBusinessService(ApplicationDbContext context) =>
        new(
            new UnitOfWork(context),
            context,
            new Mock<IInsightService>().Object,
            new Mock<IBusinessScopeResolver>().Object,
            new Mock<ISubscriptionProvisioningService>().Object,
            new Mock<IModuleEntitlementService>().Object,
            new BusinessSlugGenerator(context),
            new BusinessMembershipResolver(context),
            TestHelpers.CreateLogger<BusinessService>());

    private static InvitationService CreateInvitationService(ApplicationDbContext context)
    {
        var emailMock = new Mock<IEmailService>();
        emailMock.Setup(e => e.SendStaffInvitationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>()))
            .ReturnsAsync(true);

        return new InvitationService(
            new UnitOfWork(context),
            CreateJwtService(),
            emailMock.Object,
            Options.Create(new PublicAppSettings { BaseUrl = "http://localhost:3000", InvitationExpiryDays = 7 }),
            TestHelpers.CreateLogger<InvitationService>());
    }

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static RegisterRequest RegisterRequest(UserRole role = UserRole.Customer) => new()
    {
        Email = "new-customer@example.com",
        Password = ValidPassword,
        FullName = "New Customer",
        Role = role
    };

    private static CreateBusinessRequest BusinessRequest() => new()
    {
        Name = "Chege's Java Hut",
        Category = "Cafe",
        Location = "Nairobi",
        PhoneNumber = "+254700000001",
        Email = "cafe@example.com",
        Description = "Specialty coffee and pastries.",
        MpesaNumber = "123456"
    };

    private static User CreateOwner(string email = "owner@test.com") => new()
    {
        Id = Guid.NewGuid(),
        Email = email,
        FullName = "Test Owner",
        Role = UserRole.Business,
        CreatedAt = DateTime.UtcNow
    };

    private static Business CreateBusiness(Guid ownerId, string name = "Test Business") => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Category = "cafe",
        Location = "Nairobi",
        MpesaNumber = "123456",
        OwnerId = ownerId,
        CreatedAt = DateTime.UtcNow.AddDays(-7)
    };

    /// <summary>Canonical membership row used to seed tenant relationships in tests.</summary>
    private static BusinessMembership MembershipOf(
        User user, Guid businessId, BusinessMembershipRole role) => new()
    {
        Id = Guid.NewGuid(),
        UserId = user.Id,
        BusinessId = businessId,
        Role = role,
        Status = BusinessMembershipStatus.Active,
        JoinedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow
    };

    private static StaffInvitation CreateInvitation(Guid businessId, Guid inviterId, string email) => new()
    {
        Id = Guid.NewGuid(),
        BusinessId = businessId,
        InvitingUserId = inviterId,
        InvitedEmail = email,
        TokenHash = HashToken(TestToken),
        Status = InvitationStatus.Pending,
        ExpiresAt = DateTime.UtcNow.AddDays(7),
        CreatedAt = DateTime.UtcNow
    };

    /// <summary>An existing identity (credential + profile) that the owner invited.</summary>
    private static (UserAuth Auth, User User) CreateExistingIdentity(
        ApplicationDbContext context, string email, UserRole role = UserRole.Customer, Guid? staffBusinessId = null)
    {
        var auth = new UserAuth
        {
            Id = Guid.NewGuid(),
            Email = email,
            PasswordHash = "hash",
            IsVerified = true,
            CreatedAt = DateTime.UtcNow
        };
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            FullName = "Existing Person",
            Role = role,
            CreatedAt = DateTime.UtcNow
        };
        context.UserAuths.Add(auth);
        context.Users.Add(user);

        // A staff relationship is a membership row — never a column on the identity.
        if (staffBusinessId is Guid staffBusinessIdValue)
            context.BusinessMemberships.Add(
                MembershipOf(user, staffBusinessIdValue, BusinessMembershipRole.Staff));

        return (auth, user);
    }

    private static AcceptStaffInvitationRequest AcceptRequest() => new()
    {
        FullName = "Jane Staff",
        Password = ValidPassword,
        EmailConfirmation = InvitedEmail
    };

    /// <summary>Claim value from an access token, or null when the claim is absent.</summary>
    private static string? Claim(string accessToken, string type) =>
        new JwtSecurityTokenHandler().ReadJwtToken(accessToken).Claims
            .FirstOrDefault(c => c.Type == type)?.Value;

    /// <summary>Role claim, however the handler surfaced it (mapped or raw).</summary>
    private static string? RoleClaim(string accessToken) =>
        Claim(accessToken, ClaimTypes.Role) ?? Claim(accessToken, "role");

    // ── 1. Registration can only create a Customer ───────────────────────────

    [Theory]
    [InlineData(UserRole.Business)]
    [InlineData(UserRole.Staff)]
    [InlineData(UserRole.Admin)]
    public async Task Register_WithPrivilegedRole_IsRejectedAndCreatesNothing(UserRole role)
    {
        using var context = CreateContext($"AuthInvariants_Register_{role}");
        var service = CreateAuthService(context);

        var result = await service.RegisterAsync(RegisterRequest(role));

        Assert.False(result.Success);
        Assert.Equal("ROLE_NOT_SELF_ASSIGNABLE", result.Error?.Code);
        Assert.Empty(context.UserAuths);
        Assert.Empty(context.Users);
    }

    [Fact]
    public async Task Register_DefaultsToCustomer()
    {
        using var context = CreateContext("AuthInvariants_Register_Default");
        var service = CreateAuthService(context);

        var result = await service.RegisterAsync(RegisterRequest());

        Assert.True(result.Success, result.Error?.Message);
        var user = await context.Users.FirstAsync(u => u.Email == "new-customer@example.com");
        Assert.Equal(UserRole.Customer, user.Role);
        Assert.False(await context.BusinessMemberships.AnyAsync(m => m.UserId == user.Id));
    }

    [Theory]
    [InlineData(UserRole.Business)]
    [InlineData(UserRole.Staff)]
    [InlineData(UserRole.Admin)]
    public void RegisterValidator_RejectsPrivilegedRole(UserRole role)
    {
        var result = new RegisterRequestValidator().Validate(RegisterRequest(role));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("customer accounts only"));
    }

    [Fact]
    public void RegisterValidator_AcceptsCustomer()
    {
        var result = new RegisterRequestValidator().Validate(RegisterRequest());

        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => e.ErrorMessage)));
    }

    // ── 2. Business creation is the only owner path, and it is atomic ────────

    [Fact]
    public async Task CreateBusiness_PromotesCustomerToOwner_InOneOperation()
    {
        using var context = CreateContext("AuthInvariants_CreateBiz_Promote");
        var service = CreateAuthService(context);
        await service.RegisterAsync(RegisterRequest());
        var customer = await context.Users.FirstAsync(u => u.Email == "new-customer@example.com");
        Assert.Equal(UserRole.Customer, customer.Role);

        var result = await CreateBusinessService(context).CreateBusinessAsync(customer.Id, BusinessRequest());

        Assert.True(result.Success, result.Error?.Message);
        Assert.NotNull(result.Data);
        Assert.Equal("Chege's Java Hut", result.Data.Name);
        Assert.False(string.IsNullOrWhiteSpace(result.Data.Slug));

        var owner = await context.Users.FirstAsync(u => u.Id == customer.Id);
        Assert.Equal(UserRole.Business, owner.Role);          // promoted
        var business = await context.Businesses.SingleAsync();
        Assert.Equal(customer.Id, business.OwnerId);          // and linked
    }

    [Fact]
    public async Task CreateBusiness_RefreshIssuesOwnerTenantClaims()
    {
        using var context = CreateContext("AuthInvariants_CreateBiz_Refresh");
        var service = CreateAuthService(context);
        await service.RegisterAsync(RegisterRequest());
        var customer = await context.Users.FirstAsync(u => u.Email == "new-customer@example.com");
        var auth = await context.UserAuths.FirstAsync(a => a.Email == "new-customer@example.com");
        var jwt = CreateJwtService();

        // A session minted before provisioning carries no tenant and the
        // Customer persona — sessions are snapshots, never re-interpreted.
        var before = jwt.GenerateAccessToken(auth, customer);
        Assert.Equal(UserRole.Customer.ToString(), RoleClaim(before));
        Assert.Null(Claim(before, TenantClaimNames.BusinessId));

        var created = await CreateBusinessService(context).CreateBusinessAsync(customer.Id, BusinessRequest());
        Assert.True(created.Success, created.Error?.Message);

        var refreshValue = jwt.GenerateRefreshToken();
        await context.RefreshTokens.AddAsync(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserAuthId = auth.Id,
            Token = refreshValue,
            ExpiresAt = DateTime.UtcNow.AddDays(30),
            CreatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        // The refresh re-reads persona and tenant from the database, which is
        // why the canonical flow ends with a refresh after POST /v1/businesses.
        var businessId = created.Data!.Id;
        var refreshed = await CreateAuthService(context, businessId)
            .RefreshTokenAsync(refreshValue, "chege-s-java-hut");

        Assert.True(refreshed.Success, refreshed.Error?.Message);
        Assert.NotNull(refreshed.Data);
        Assert.Equal(UserRole.Business.ToString(), RoleClaim(refreshed.Data.AccessToken));
        Assert.Equal(businessId.ToString(), Claim(refreshed.Data.AccessToken, TenantClaimNames.BusinessId));
        Assert.Equal(TenantClaimNames.RoleOwner, Claim(refreshed.Data.AccessToken, TenantClaimNames.BusinessRole));
    }

    [Theory]
    [InlineData(UserRole.Admin)]
    public async Task CreateBusiness_RefusesPlatformAdmin(UserRole role)
    {
        using var context = CreateContext($"AuthInvariants_CreateBiz_{role}");
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = "not-a-customer@example.com",
            FullName = "Not A Customer",
            Role = role,
            CreatedAt = DateTime.UtcNow
        };
        await context.Users.AddAsync(user);
        await context.SaveChangesAsync();

        var result = await CreateBusinessService(context).CreateBusinessAsync(user.Id, BusinessRequest());

        Assert.False(result.Success);
        Assert.Equal("ROLE_NOT_ELIGIBLE", result.Error?.Code);
        Assert.Empty(context.Businesses);
        Assert.Equal(role, (await context.Users.FirstAsync(u => u.Id == user.Id)).Role);
    }

    [Fact]
    public async Task CreateBusiness_RefusesExistingOwner()
    {
        using var context = CreateContext("AuthInvariants_CreateBiz_Existing");
        var owner = CreateOwner();
        var business = CreateBusiness(owner.Id);
        await context.Users.AddAsync(owner);
        await context.Businesses.AddAsync(business);
        await context.BusinessMemberships.AddAsync(
            MembershipOf(owner, business.Id, BusinessMembershipRole.Owner));
        await context.SaveChangesAsync();

        var result = await CreateBusinessService(context).CreateBusinessAsync(owner.Id, BusinessRequest());

        Assert.False(result.Success);
        Assert.Equal("BUSINESS_EXISTS", result.Error?.Code);
        Assert.Single(context.Businesses);
    }

    [Fact]
    public async Task CreateBusiness_RefusesSoftDeletedIdentity()
    {
        using var context = CreateContext("AuthInvariants_CreateBiz_Deleted");
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = "deleted@example.com",
            FullName = "Deleted Person",
            Role = UserRole.Customer,
            IsDeleted = true,
            CreatedAt = DateTime.UtcNow
        };
        await context.Users.AddAsync(user);
        await context.SaveChangesAsync();

        // User carries a global query filter (ApplicationDbContext:
        // HasQueryFilter(u => !u.IsDeleted)), so a disabled identity is simply
        // not an account any more — it can never provision a business.
        var result = await CreateBusinessService(context).CreateBusinessAsync(user.Id, BusinessRequest());

        Assert.False(result.Success);
        Assert.Equal("NOT_FOUND", result.Error?.Code);
        Assert.Empty(context.Businesses);
    }

    // ── 3. Staff exists only through an owner-issued invitation ──────────────

    private static async Task<(ApplicationDbContext Context, User Owner, Business Business, StaffInvitation Invitation)>
        SeedInvitationAsync(string dbName, string invitedEmail = InvitedEmail)
    {
        var context = CreateContext(dbName);
        var owner = CreateOwner();
        var business = CreateBusiness(owner.Id);
        var invitation = CreateInvitation(business.Id, owner.Id, invitedEmail);
        await context.Users.AddAsync(owner);
        await context.Businesses.AddAsync(business);
        await context.BusinessMemberships.AddAsync(
            MembershipOf(owner, business.Id, BusinessMembershipRole.Owner));
        await context.StaffInvitations.AddAsync(invitation);
        await context.SaveChangesAsync();
        return (context, owner, business, invitation);
    }

    [Fact]
    public async Task ExistingAccountAccept_LinksStaffAndIssuesTenantAwareToken()
    {
        var seeded = await SeedInvitationAsync("AuthInvariants_Invite_Existing_Success");
        using var context = seeded.Context;
        var (auth, user) = CreateExistingIdentity(context, InvitedEmail);
        await context.SaveChangesAsync();

        var result = await CreateInvitationService(context)
            .AcceptStaffInvitationForExistingAccountAsync(auth.Id, TestToken);

        Assert.True(result.Success, result.Error?.Message);
        Assert.NotNull(result.Data);
        Assert.Equal(UserRole.Staff, result.Data.User.Role);
        Assert.NotEmpty(result.Data.RefreshToken);

        // Tenant-aware tokens: without these the new session is tenant-less
        // until its first refresh, and every business-scoped call would fail.
        Assert.Equal(seeded.Business.Id.ToString(), Claim(result.Data.AccessToken, TenantClaimNames.BusinessId));
        Assert.Equal(TenantClaimNames.RoleStaff, Claim(result.Data.AccessToken, TenantClaimNames.BusinessRole));

        var linked = await context.Users.FirstAsync(u => u.Id == user.Id);
        Assert.Equal(UserRole.Staff, linked.Role);
        var membership = await context.BusinessMemberships.SingleAsync(m =>
            m.UserId == user.Id && m.BusinessId == seeded.Business.Id);
        Assert.Equal(BusinessMembershipRole.Staff, membership.Role);
        Assert.Equal(BusinessMembershipStatus.Active, membership.Status);

        var accepted = await context.StaffInvitations.FirstAsync(i => i.Id == seeded.Invitation.Id);
        Assert.Equal(InvitationStatus.Accepted, accepted.Status);
        Assert.NotNull(accepted.AcceptedAt);

        Assert.Single(context.RefreshTokens);
        // No duplicate identity was created for the invited address.
        Assert.Single(context.Users.Where(u => u.Email == InvitedEmail));
        Assert.Single(context.UserAuths.Where(a => a.Email == InvitedEmail));
    }

    [Fact]
    public async Task ExistingAccountAccept_WithDifferentSessionEmail_IsRefused()
    {
        var seeded = await SeedInvitationAsync("AuthInvariants_Invite_Existing_Mismatch");
        using var context = seeded.Context;
        var (auth, user) = CreateExistingIdentity(context, "someone-else@example.com");
        await context.SaveChangesAsync();

        var result = await CreateInvitationService(context)
            .AcceptStaffInvitationForExistingAccountAsync(auth.Id, TestToken);

        Assert.False(result.Success);
        Assert.Equal("EMAIL_MISMATCH", result.Error?.Code);

        // Nothing was promoted: the token alone never attaches to an identity.
        var unchanged = await context.Users.FirstAsync(u => u.Id == user.Id);
        Assert.Equal(UserRole.Customer, unchanged.Role);
        Assert.False(await context.BusinessMemberships.AnyAsync(m => m.UserId == user.Id));
        Assert.Equal(InvitationStatus.Pending,
            (await context.StaffInvitations.FirstAsync(i => i.Id == seeded.Invitation.Id)).Status);
        Assert.Empty(context.RefreshTokens);
    }

    [Fact]
    public async Task ExistingAccountAccept_OwnerIsRefused()
    {
        var seeded = await SeedInvitationAsync("AuthInvariants_Invite_Existing_Owner");
        using var context = seeded.Context;
        // The inviting owner signs in and tries to accept their own invitation.
        var ownerAuth = new UserAuth
        {
            Id = Guid.NewGuid(),
            Email = seeded.Owner.Email,
            PasswordHash = "hash",
            IsVerified = true,
            CreatedAt = DateTime.UtcNow
        };
        await context.UserAuths.AddAsync(ownerAuth);
        await context.SaveChangesAsync();

        var result = await CreateInvitationService(context)
            .AcceptStaffInvitationForExistingAccountAsync(ownerAuth.Id, TestToken);

        Assert.False(result.Success);
        Assert.Equal("EMAIL_MISMATCH", result.Error?.Code); // owner email is not the invited email
    }

    [Fact]
    public async Task ExistingAccountAccept_StaffAlreadyAtSameBusiness_IsRefused()
    {
        var seeded = await SeedInvitationAsync("AuthInvariants_Invite_Existing_AlreadyStaff");
        using var context = seeded.Context;
        var (auth, _) = CreateExistingIdentity(
            context, InvitedEmail, UserRole.Staff, staffBusinessId: seeded.Business.Id);
        await context.SaveChangesAsync();

        var result = await CreateInvitationService(context)
            .AcceptStaffInvitationForExistingAccountAsync(auth.Id, TestToken);

        Assert.False(result.Success);
        Assert.Equal("ALREADY_STAFF", result.Error?.Code);
        Assert.Equal(InvitationStatus.Pending,
            (await context.StaffInvitations.FirstAsync(i => i.Id == seeded.Invitation.Id)).Status);
    }

    [Fact]
    public async Task ExistingAccountAccept_StaffOfAnotherBusiness_AddsASecondMembership()
    {
        var seeded = await SeedInvitationAsync("AuthInvariants_Invite_Existing_StaffElsewhere");
        using var context = seeded.Context;
        var otherBusinessId = Guid.NewGuid();
        var (auth, user) = CreateExistingIdentity(
            context, InvitedEmail, UserRole.Staff, staffBusinessId: otherBusinessId);
        await context.SaveChangesAsync();

        var result = await CreateInvitationService(context)
            .AcceptStaffInvitationForExistingAccountAsync(auth.Id, TestToken);

        Assert.True(result.Success, result.Error?.Message);

        // Multi-tenant: the identity keeps its other staff role and gains this one.
        Assert.Equal(2, await context.BusinessMemberships.CountAsync(m =>
            m.UserId == user.Id && m.Status == BusinessMembershipStatus.Active));
        Assert.True(await context.BusinessMemberships.AnyAsync(m =>
            m.UserId == user.Id &&
            m.BusinessId == seeded.Business.Id &&
            m.Role == BusinessMembershipRole.Staff &&
            m.Status == BusinessMembershipStatus.Active));
        Assert.Equal(TenantClaimNames.RoleStaff, Claim(result.Data!.AccessToken, TenantClaimNames.BusinessRole));
        Assert.Equal(seeded.Business.Id.ToString(), Claim(result.Data.AccessToken, TenantClaimNames.BusinessId));
    }

    [Fact]
    public async Task ExistingAccountAccept_BusinessOwner_IsRefused()
    {
        using var context = CreateContext("AuthInvariants_Invite_Existing_OwnsBusiness");
        var owner = CreateOwner();
        var ownBusiness = CreateBusiness(owner.Id, "First Business");
        var invitingBusiness = CreateBusiness(Guid.NewGuid(), "Second Business");
        var invitation = CreateInvitation(invitingBusiness.Id, Guid.NewGuid(), owner.Email);
        var ownerAuth = new UserAuth
        {
            Id = Guid.NewGuid(),
            Email = owner.Email,
            PasswordHash = "hash",
            IsVerified = true,
            CreatedAt = DateTime.UtcNow
        };
        await context.Users.AddAsync(owner);
        await context.Businesses.AddRangeAsync(ownBusiness, invitingBusiness);
        await context.BusinessMemberships.AddAsync(
            MembershipOf(owner, ownBusiness.Id, BusinessMembershipRole.Owner));
        await context.StaffInvitations.AddAsync(invitation);
        await context.UserAuths.AddAsync(ownerAuth);
        await context.SaveChangesAsync();

        var result = await CreateInvitationService(context)
            .AcceptStaffInvitationForExistingAccountAsync(ownerAuth.Id, TestToken);

        Assert.False(result.Success);
        Assert.Equal("OWNS_BUSINESS", result.Error?.Code);
        var after = await context.Users.FirstAsync(u => u.Id == owner.Id);
        Assert.Equal(UserRole.Business, after.Role);
        // No staff membership was granted by the refused acceptance.
        Assert.False(await context.BusinessMemberships.AnyAsync(m =>
            m.UserId == owner.Id && m.BusinessId == invitingBusiness.Id));
    }

    [Fact]
    public async Task ExistingAccountAccept_Admin_IsRefused()
    {
        var seeded = await SeedInvitationAsync("AuthInvariants_Invite_Existing_Admin");
        using var context = seeded.Context;
        var (auth, _) = CreateExistingIdentity(context, InvitedEmail, UserRole.Admin);
        await context.SaveChangesAsync();

        var result = await CreateInvitationService(context)
            .AcceptStaffInvitationForExistingAccountAsync(auth.Id, TestToken);

        Assert.False(result.Success);
        Assert.Equal("ROLE_NOT_ELIGIBLE", result.Error?.Code);
    }

    [Fact]
    public async Task PublicAccept_WhenIdentityAlreadyExists_ReturnsEmailInUse()
    {
        var seeded = await SeedInvitationAsync("AuthInvariants_Invite_Public_Existing");
        using var context = seeded.Context;
        var (_, user) = CreateExistingIdentity(context, InvitedEmail);
        await context.SaveChangesAsync();

        var result = await CreateInvitationService(context)
            .AcceptStaffInvitationAsync(TestToken, AcceptRequest());

        Assert.False(result.Success);
        Assert.Equal("EMAIL_IN_USE", result.Error?.Code);
        // The public path never creates a second identity nor links the first.
        Assert.Single(context.UserAuths.Where(a => a.Email == InvitedEmail));
        Assert.Single(context.Users.Where(u => u.Email == InvitedEmail));
        Assert.Equal(UserRole.Customer, (await context.Users.FirstAsync(u => u.Id == user.Id)).Role);
        Assert.Equal(InvitationStatus.Pending,
            (await context.StaffInvitations.FirstAsync(i => i.Id == seeded.Invitation.Id)).Status);
        Assert.Empty(context.RefreshTokens);
    }

    [Fact]
    public async Task ExistingAccountAccept_UsedInvitation_IsRefused()
    {
        var seeded = await SeedInvitationAsync("AuthInvariants_Invite_Existing_Used");
        using var context = seeded.Context;
        var (auth, _) = CreateExistingIdentity(context, InvitedEmail, UserRole.Customer);
        var invitation = await context.StaffInvitations.FirstAsync(i => i.Id == seeded.Invitation.Id);
        invitation.Status = InvitationStatus.Accepted;
        invitation.AcceptedAt = DateTime.UtcNow.AddMinutes(-1);
        await context.SaveChangesAsync();

        var result = await CreateInvitationService(context)
            .AcceptStaffInvitationForExistingAccountAsync(auth.Id, TestToken);

        Assert.False(result.Success);
        Assert.Equal("ALREADY_USED", result.Error?.Code);
    }

    [Fact]
    public async Task ExistingAccountAccept_DeletedBusiness_IsRefused()
    {
        var seeded = await SeedInvitationAsync("AuthInvariants_Invite_Existing_DeletedBiz");
        using var context = seeded.Context;
        var (auth, _) = CreateExistingIdentity(context, InvitedEmail, UserRole.Customer);
        var business = await context.Businesses.FirstAsync(b => b.Id == seeded.Business.Id);
        business.IsDeleted = true;
        await context.SaveChangesAsync();

        var result = await CreateInvitationService(context)
            .AcceptStaffInvitationForExistingAccountAsync(auth.Id, TestToken);

        Assert.False(result.Success);
        Assert.Equal("BUSINESS_NOT_FOUND", result.Error?.Code);
    }

    // ── Model shape: the refactor cannot silently regress ─────────

    /// <summary>
    /// Tenant authority lives in <c>business_memberships</c> only. This guards the
    /// removal of <c>users."StaffBusinessId"</c> (migration
    /// <c>RemoveLegacyStaffBusinessId</c>) and the single-owner / one-row-per-pair
    /// indexes that make the service's guarantees race-safe.
    /// </summary>
    [Fact]
    public void Model_KeepsTenantAuthorityInMembershipsOnly()
    {
        // The legacy pointer is gone for good: no code may read a tenant off the user.
        Assert.Null(typeof(User).GetProperty("StaffBusinessId"));

        var navigation = typeof(User).GetProperty(nameof(User.BusinessMemberships));
        Assert.NotNull(navigation);
        Assert.True(typeof(ICollection<BusinessMembership>).IsAssignableFrom(navigation!.PropertyType));

        using var context = CreateContext("AuthInvariants_Model_Shape");

        var userEntity = context.Model.FindEntityType(typeof(User))!;
        Assert.Null(userEntity.FindProperty("StaffBusinessId"));

        var membershipEntity = context.Model.FindEntityType(typeof(BusinessMembership))!;
        Assert.Equal("business_memberships", membershipEntity.GetTableName());

        var uniquePair = membershipEntity.GetIndexes().Single(i =>
            i.Properties.Select(p => p.Name).SequenceEqual(new[] { "UserId", "BusinessId" }));
        Assert.True(uniquePair.IsUnique);

        var singleOwner = membershipEntity.GetIndexes().Single(i =>
            i.Properties.Count == 1 && i.Properties[0].Name == nameof(BusinessMembership.BusinessId));
        Assert.True(singleOwner.IsUnique);
        Assert.Equal("role = 'Owner' AND status = 'Active'", singleOwner.GetFilter());

        // OwnerId survives as a denormalized pointer, never as the membership itself.
        Assert.NotNull(typeof(Business).GetProperty(nameof(Business.OwnerId)));
    }
}

