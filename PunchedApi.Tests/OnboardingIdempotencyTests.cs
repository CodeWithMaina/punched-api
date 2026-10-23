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
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;
using PunchedApi.Infrastructure.Repositories;

namespace PunchedApi.Tests;

/// <summary>
/// Onboarding replay-safety tests. The onboarding endpoints are NOT keyed off the client
/// <c>Idempotency-Key</c> header (that store is per-user and cannot represent anonymous signup);
/// replay safety is instead guaranteed structurally: unique email, duplicate business-name guard,
/// single-use verification codes and single-use invitation tokens. These tests pin that behaviour
/// so a client retry — double submit, refresh, offline replay — can never create a duplicate
/// account, business or staff membership. Rollback guarantees are proven by asserting row counts
/// after a rejected replay.
/// </summary>
public class OnboardingIdempotencyTests
{
    private const string TestToken = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    // ── Fixtures ────────────────────────────────────────────────

    private static ApplicationDbContext CreateContext(string name)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(name)
            .Options;
        return new ApplicationDbContext(options);
    }

    private static JwtTokenService CreateJwtService()
        => new(Options.Create(new JwtSettings
        {
            Secret = "this-is-a-test-secret-that-is-at-least-32-characters-long",
            Issuer = "PunchedApi-Test",
            Audience = "PunchedApi-Tests",
            AccessTokenExpiryMinutes = 60,
            RefreshTokenExpiryDays = 30
        }));

    private static IMapper CreateMapper()
        => new MapperConfiguration(cfg => cfg.AddProfile<MappingProfile>()).CreateMapper();

    private static AuthService CreateAuthService(ApplicationDbContext context, Mock<IEmailService>? emailMock = null)
    {
        emailMock ??= new Mock<IEmailService>();
        emailMock.Setup(e => e.SendVerificationCodeAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        return new AuthService(
            new UnitOfWork(context),
            CreateJwtService(),
            emailMock.Object,
            CreateMapper(),
            TestHelpers.CreateLogger<AuthService>(),
            new SubscriptionProvisioningService(context, TestHelpers.CreateLogger<SubscriptionProvisioningService>()),
            new BusinessSlugGenerator(context),
            // Tenant-aware auth: real membership view over Owner/Staff/Enrollment;
            // host resolution is never exercised without a businessSlug here.
            new BusinessMembershipResolver(context),
            new Mock<ITenantHostResolver>().Object);
    }

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

    private static RegisterRequest CustomerRequest(string email = "customer@example.com") => new()
    {
        Email = email,
        Password = "P@ssw0rd!12",
        FullName = "Amina Customer"
    };

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

    private static CreateBusinessRequest BusinessRequest() => new()
    {
        Name = "Chege's Java Hut",
        Category = "Cafe",
        Location = "Nairobi",
        PhoneNumber = "+254700000001",
        Email = "cafe@example.com",
        MpesaNumber = "123456",
        Description = "Specialty coffee and pastries."
    };

    private static string HashCode(string code)
        => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(code)));

    private static string HashToken(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static (User Owner, Business Business, StaffInvitation Invitation) SeedInvitation(
        string email = "staff@example.com", string token = TestToken)
    {
        var owner = new User
        {
            Id = Guid.NewGuid(),
            Email = "owner@test.com",
            FullName = "Test Owner",
            Role = UserRole.Business,
            CreatedAt = DateTime.UtcNow
        };
        var business = new Business
        {
            Id = Guid.NewGuid(),
            Name = "Test Business",
            Category = "cafe",
            Location = "Nairobi",
            MpesaNumber = "123456",
            OwnerId = owner.Id,
            CreatedAt = DateTime.UtcNow.AddDays(-7)
        };
        var invitation = new StaffInvitation
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            InvitingUserId = owner.Id,
            InvitedEmail = email,
            TokenHash = HashToken(token),
            Status = InvitationStatus.Pending,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            CreatedAt = DateTime.UtcNow
        };
        return (owner, business, invitation);
    }

    private static AcceptStaffInvitationRequest AcceptRequest(string email = "staff@example.com") => new()
    {
        FullName = "Jane Staff",
        Password = "P@ssw0rd!12",
        EmailConfirmation = email
    };


    // ── Customer registration replay ────────────────────────────

    [Fact]
    public async Task CustomerRegister_ReplayWithSameEmail_IsRejectedAndKeepsSingleAccount()
    {
        using var context = CreateContext("Onboarding_CustomerReplay_01");
        var service = CreateAuthService(context);

        var first = await service.RegisterAsync(CustomerRequest());
        Assert.True(first.Success, first.Error?.Message);
        Assert.Equal(UserRole.Customer, (await context.Users.FirstAsync(u => u.Email == "customer@example.com")).Role);

        // Retry the same submission — and try to escalate the role while at it.
        var retriedWithPrivilege = CustomerRequest();
        retriedWithPrivilege.Role = UserRole.Business;
        var replay = await service.RegisterAsync(retriedWithPrivilege);

        Assert.False(replay.Success);
        Assert.Equal("EMAIL_ALREADY_REGISTERED", replay.Error?.Code);
        Assert.Equal(1, await context.UserAuths.CountAsync(a => a.Email == "customer@example.com"));
        Assert.Equal(1, await context.Users.CountAsync(u => u.Email == "customer@example.com"));
        // A public signup can never mint a privileged role.
        Assert.Equal(UserRole.Customer, (await context.Users.FirstAsync(u => u.Email == "customer@example.com")).Role);
    }

    // ── Business provisioning replay ────────────────────────────

    [Fact]
    public async Task CreateBusiness_ReplayAfterSuccess_CreatesNoSecondBusinessOrMembership()
    {
        using var context = CreateContext("Onboarding_BusinessReplay_01");
        var owner = new User
        {
            Id = Guid.NewGuid(),
            Email = "owner@example.com",
            FullName = "Jane Chege",
            Role = UserRole.Customer,
            CreatedAt = DateTime.UtcNow
        };
        context.Users.Add(owner);
        await context.SaveChangesAsync();

        var service = CreateBusinessService(context);
        var first = await service.CreateBusinessAsync(owner.Id, BusinessRequest());
        Assert.True(first.Success, first.Error?.Message);

        var replay = await service.CreateBusinessAsync(owner.Id, BusinessRequest());

        Assert.False(replay.Success);
        Assert.Equal("BUSINESS_EXISTS", replay.Error?.Code);
        Assert.Equal(1, await context.Businesses.CountAsync(b => b.Name == "Chege's Java Hut"));
        Assert.Equal(1, await context.BusinessMemberships.CountAsync(m => m.UserId == owner.Id));
    }

    [Fact]
    public async Task CreateBusiness_WithSameName_CreatesASecondBusinessForADifferentOwner()
    {
        using var context = CreateContext("Onboarding_BusinessNameReplay_01");
        var first = new User
        {
            Id = Guid.NewGuid(), Email = "one@example.com", FullName = "One",
            Role = UserRole.Customer, CreatedAt = DateTime.UtcNow
        };
        var second = new User
        {
            Id = Guid.NewGuid(), Email = "two@example.com", FullName = "Two",
            Role = UserRole.Customer, CreatedAt = DateTime.UtcNow
        };
        context.Users.AddRange(first, second);
        await context.SaveChangesAsync();

        var service = CreateBusinessService(context);
        Assert.True((await service.CreateBusinessAsync(first.Id, BusinessRequest())).Success);
        Assert.True((await service.CreateBusinessAsync(second.Id, BusinessRequest())).Success);

        // Names are not unique across owners; the slug generator disambiguates.
        var businesses = await context.Businesses.Where(b => b.Name == "Chege's Java Hut").ToListAsync();
        Assert.Equal(2, businesses.Count);
        Assert.Equal(2, businesses.Select(b => b.Slug).Distinct().Count());
        Assert.Equal(2, await context.BusinessMemberships.CountAsync());
    }

    // ── Email verification is single-use ────────────────────────

    [Fact]
    public async Task VerifyEmail_ReplayAfterSuccess_ReturnsAlreadyVerified()
    {
        using var context = CreateContext("Onboarding_VerifyReplay_01");
        const string code = "123456";
        var authId = Guid.NewGuid();
        await context.UserAuths.AddAsync(new UserAuth
        {
            Id = authId,
            Email = "customer@example.com",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("P@ssw0rd!12", 4),
            IsVerified = false,
            VerificationCode = HashCode(code),
            VerificationCodeExpiresAt = DateTime.UtcNow.AddMinutes(10),
            VerificationCodeAttempts = 0,
            CreatedAt = DateTime.UtcNow
        });
        await context.Users.AddAsync(new User
        {
            Id = Guid.NewGuid(),
            Email = "customer@example.com",
            FullName = "Amina Customer",
            Role = UserRole.Customer,
            CreatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
        var service = CreateAuthService(context);

        var first = await service.VerifyEmailAsync(new VerifyEmailRequest { Email = "customer@example.com", Code = code });
        Assert.True(first.Success, first.Error?.Message);

        var replay = await service.VerifyEmailAsync(new VerifyEmailRequest { Email = "customer@example.com", Code = code });

        Assert.False(replay.Success);
        Assert.Equal("ALREADY_VERIFIED", replay.Error?.Code);
        var stored = await context.UserAuths.FirstAsync(a => a.Id == authId);
        Assert.Null(stored.VerificationCode); // code consumed, cannot be replayed
    }

    // ── Invitation acceptance replay ────────────────────────────

    [Fact]
    public async Task AcceptInvitation_ReplayAfterSuccess_ReturnsAlreadyUsedAndKeepsOneStaffAccount()
    {
        using var context = CreateContext("Onboarding_InviteReplay_01");
        var (owner, business, invitation) = SeedInvitation();
        await context.Users.AddAsync(owner);
        await context.Businesses.AddAsync(business);
        await context.BusinessMemberships.AddAsync(new BusinessMembership
        {
            Id = Guid.NewGuid(),
            UserId = owner.Id,
            BusinessId = business.Id,
            Role = BusinessMembershipRole.Owner,
            Status = BusinessMembershipStatus.Active,
            JoinedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        });
        await context.StaffInvitations.AddAsync(invitation);
        await context.SaveChangesAsync();
        var service = CreateInvitationService(context);

        var first = await service.AcceptStaffInvitationAsync(TestToken, AcceptRequest());
        Assert.True(first.Success, first.Error?.Message);

        var replay = await service.AcceptStaffInvitationAsync(TestToken, AcceptRequest());

        Assert.False(replay.Success);
        Assert.Equal("ALREADY_USED", replay.Error?.Code);
        Assert.Equal(1, await context.UserAuths.CountAsync(a => a.Email == "staff@example.com"));
        Assert.Equal(1, await context.Users.CountAsync(u => u.Email == "staff@example.com"));
        var accepted = await context.StaffInvitations.FirstAsync(i => i.Id == invitation.Id);
        Assert.Equal(InvitationStatus.Accepted, accepted.Status);
        Assert.NotNull(accepted.AcceptedAt);
        var staffUser = await context.Users.FirstAsync(u => u.Email == "staff@example.com");
        Assert.True(await context.BusinessMemberships.AnyAsync(m =>
            m.UserId == staffUser.Id &&
            m.BusinessId == business.Id &&
            m.Role == BusinessMembershipRole.Staff &&
            m.Status == BusinessMembershipStatus.Active));
    }

    [Fact]
    public async Task AcceptInvitation_AfterResend_PreviousTokenIsInvalid()
    {
        using var context = CreateContext("Onboarding_InviteRotation_01");
        var (owner, business, invitation) = SeedInvitation();
        await context.Users.AddAsync(owner);
        await context.Businesses.AddAsync(business);
        await context.BusinessMemberships.AddAsync(new BusinessMembership
        {
            Id = Guid.NewGuid(),
            UserId = owner.Id,
            BusinessId = business.Id,
            Role = BusinessMembershipRole.Owner,
            Status = BusinessMembershipStatus.Active,
            JoinedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        });
        await context.StaffInvitations.AddAsync(invitation);
        await context.SaveChangesAsync();
        var service = CreateInvitationService(context);

        var resend = await service.ResendStaffInvitationAsync(owner.Id, invitation.Id);
        Assert.True(resend.Success, resend.Error?.Message);

        // Rotation must invalidate the previously emailed link — one live token per invite.
        var stale = await service.AcceptStaffInvitationAsync(TestToken, AcceptRequest());

        Assert.False(stale.Success);
        Assert.Equal("INVALID_TOKEN", stale.Error?.Code);
        Assert.Equal(0, await context.UserAuths.CountAsync());
    }

    [Fact]
    public async Task AcceptInvitation_WrongEmailConfirmation_IsRejectedAndCreatesNoAccount()
    {
        using var context = CreateContext("Onboarding_InviteEmailMismatch_01");
        var (owner, business, invitation) = SeedInvitation();
        await context.Users.AddAsync(owner);
        await context.Businesses.AddAsync(business);
        await context.BusinessMemberships.AddAsync(new BusinessMembership
        {
            Id = Guid.NewGuid(),
            UserId = owner.Id,
            BusinessId = business.Id,
            Role = BusinessMembershipRole.Owner,
            Status = BusinessMembershipStatus.Active,
            JoinedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        });
        await context.StaffInvitations.AddAsync(invitation);
        await context.SaveChangesAsync();
        var service = CreateInvitationService(context);

        var result = await service.AcceptStaffInvitationAsync(TestToken, AcceptRequest("someone-else@example.com"));

        Assert.False(result.Success);
        Assert.Equal("EMAIL_MISMATCH", result.Error?.Code);
        Assert.Equal(0, await context.UserAuths.CountAsync());
        Assert.Equal(0, await context.Users.CountAsync(u => u.Role == UserRole.Staff));
    }

}
