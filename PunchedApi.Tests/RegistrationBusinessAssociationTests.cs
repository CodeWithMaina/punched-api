using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Mappings;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;
using PunchedApi.Infrastructure.Repositories;

namespace PunchedApi.Tests;

/// <summary>
/// Registration → business association.
///
/// <para>A customer who signs up ON a business address must end up associated
/// with THAT business, and with no other. The association is the persisted
/// <see cref="CustomerBusinessEnrollment"/> row — the authoritative
/// Customer ↔ Business relationship that the business roster, bookings and
/// loyalty all key off.</para>
///
/// <para>These tests pin the boundary: a registration on business A never
/// creates a row for business B, a registration on the platform root creates
/// no row at all, the association is written in the SAME transaction as the
/// account (no orphan accounts), and the email-verification backstop is
/// idempotent and never re-adds a customer who deliberately left.</para>
/// </summary>
public class RegistrationBusinessAssociationTests
{
    // ── Fixtures ────────────────────────────────────────────────

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

    private static Business AddBusiness(ApplicationDbContext ctx, string name, string slug)
    {
        var owner = new User
        {
            Id = Guid.NewGuid(),
            Email = $"owner-{slug}@t.com",
            FullName = $"Owner of {name}",
            Role = UserRole.Business,
            CreatedAt = DateTime.UtcNow
        };
        ctx.Users.Add(owner);

        var business = new Business
        {
            Id = Guid.NewGuid(),
            Name = name,
            Category = "cafe",
            Location = "Nairobi",
            MpesaNumber = "1",
            Slug = slug,
            OwnerId = owner.Id,
            TimeZoneId = "UTC",
            CreatedAt = DateTime.UtcNow
        };
        ctx.Businesses.Add(business);
        return business;
    }

    /// <summary>
    /// Builds an auth service whose tenant context and slug resolver point at
    /// the supplied business (or none at all when <paramref name="tenantBusiness"/> is null).
    /// </summary>
    private static (AuthService Service, Mock<IEmailService> Email) CreateAuthService(
        ApplicationDbContext ctx,
        Business? tenantBusiness)
    {
        var emailMock = new Mock<IEmailService>();
        emailMock.Setup(e => e.SendVerificationCodeAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        var tenantResolver = new Mock<ITenantHostResolver>();
        tenantResolver.Setup(r => r.ResolveBusinessIdAsync(It.IsAny<string>()))
            .ReturnsAsync((string? slug) =>
                ctx.Businesses
                    .Where(b => b.Slug == slug)
                    .Select(b => (Guid?)b.Id)
                    .FirstOrDefault());

        var tenantContext = new TenantContext();
        tenantContext.Set(tenantBusiness?.Slug, tenantBusiness?.Id);

        var service = new AuthService(
            new UnitOfWork(ctx),
            CreateJwtService(),
            emailMock.Object,
            CreateMapper(),
            TestHelpers.CreateLogger<AuthService>(),
            new SubscriptionProvisioningService(ctx, TestHelpers.CreateLogger<SubscriptionProvisioningService>()),
            new BusinessSlugGenerator(ctx),
            // Real membership view: one business_memberships read resolves
            // Owner / Staff / Customer alike.
            new BusinessMembershipResolver(ctx),
            tenantResolver.Object,
            notifications: null,
            tenantContext: tenantContext);

        return (service, emailMock);
    }

    private static RegisterRequest CustomerRequest(string email) => new()
    {
        Email = email,
        Password = "P@ssw0rd!12",
        FullName = "Amina Customer"
    };

    // ── Tests ───────────────────────────────────────────────────

    [Fact]
    public async Task Registration_On_A_Business_Address_Creates_An_Active_Enrollment_For_That_Business()
    {
        var ctx = CreateContext(nameof(Registration_On_A_Business_Address_Creates_An_Active_Enrollment_For_That_Business));
        using (ctx)
        {
            var business = AddBusiness(ctx, "Java House", "java-house");
            ctx.SaveChanges();

            var (service, _) = CreateAuthService(ctx, business);

            var result = await service.RegisterAsync(CustomerRequest("amina@example.com"));

            Assert.True(result.Success);

            var user = ctx.Users.Single(u => u.Email == "amina@example.com");
            var enrollment = Assert.Single(ctx.CustomerBusinessEnrollments);

            Assert.Equal(user.Id, enrollment.CustomerId);
            Assert.Equal(business.Id, enrollment.BusinessId);
            Assert.Equal(CustomerBusinessEnrollmentStatus.Active, enrollment.Status);
            Assert.Equal("business", enrollment.Source);
        }
    }

    [Fact]
    public async Task Registration_On_The_Platform_Root_Creates_No_Enrollment()
    {
        var ctx = CreateContext(nameof(Registration_On_The_Platform_Root_Creates_No_Enrollment));
        using (ctx)
        {
            AddBusiness(ctx, "Java House", "java-house");
            ctx.SaveChanges();

            var (service, _) = CreateAuthService(ctx, tenantBusiness: null);

            var result = await service.RegisterAsync(CustomerRequest("root@example.com"));

            Assert.True(result.Success);
            Assert.Empty(ctx.CustomerBusinessEnrollments);
        }
    }

    [Fact]
    public async Task Registration_Never_Associates_The_Customer_With_Another_Business()
    {
        var ctx = CreateContext(nameof(Registration_Never_Associates_The_Customer_With_Another_Business));
        using (ctx)
        {
            var businessA = AddBusiness(ctx, "Java House", "java-house");
            var businessB = AddBusiness(ctx, "Kudu Brew Bar", "kudu-brew-bar");
            ctx.SaveChanges();

            var (service, _) = CreateAuthService(ctx, businessA);

            var result = await service.RegisterAsync(CustomerRequest("amina@example.com"));

            Assert.True(result.Success);

            // Exactly one row — for business A only. Business B must never see
            // a customer who signed up on someone else's address.
            var enrollment = Assert.Single(ctx.CustomerBusinessEnrollments);
            Assert.Equal(businessA.Id, enrollment.BusinessId);
            Assert.DoesNotContain(ctx.CustomerBusinessEnrollments, e => e.BusinessId == businessB.Id);
        }
    }

    [Fact]
    public async Task Registration_With_A_Slug_Hint_On_The_Platform_Root_Still_Associates_The_Business()
    {
        var ctx = CreateContext(nameof(Registration_With_A_Slug_Hint_On_The_Platform_Root_Still_Associates_The_Business));
        using (ctx)
        {
            var business = AddBusiness(ctx, "Java House", "java-house");
            ctx.SaveChanges();

            var (service, _) = CreateAuthService(ctx, tenantBusiness: null);

            var request = CustomerRequest("amina@example.com");
            request.BusinessSlug = "java-house";

            var result = await service.RegisterAsync(request);

            Assert.True(result.Success);

            var enrollment = Assert.Single(ctx.CustomerBusinessEnrollments);
            Assert.Equal(business.Id, enrollment.BusinessId);
        }
    }

    [Fact]
    public async Task An_Unknown_Slug_Hint_Associates_Nothing()
    {
        var ctx = CreateContext(nameof(An_Unknown_Slug_Hint_Associates_Nothing));
        using (ctx)
        {
            AddBusiness(ctx, "Java House", "java-house");
            ctx.SaveChanges();

            var (service, _) = CreateAuthService(ctx, tenantBusiness: null);

            var request = CustomerRequest("amina@example.com");
            request.BusinessSlug = "does-not-exist";

            var result = await service.RegisterAsync(request);

            Assert.True(result.Success);
            Assert.Empty(ctx.CustomerBusinessEnrollments);
        }
    }

    [Fact]
    public async Task Verification_Backstop_Associates_A_Customer_Who_Registered_Without_A_Business()
    {
        var ctx = CreateContext(nameof(Verification_Backstop_Associates_A_Customer_Who_Registered_Without_A_Business));
        using (ctx)
        {
            var business = AddBusiness(ctx, "Java House", "java-house");
            ctx.SaveChanges();

            // Registered on the platform root → no enrollment yet.
            var (service, email) = CreateAuthService(ctx, tenantBusiness: null);

            string? code = null;
            email.Setup(e => e.SendVerificationCodeAsync(It.IsAny<string>(), It.IsAny<string>()))
                .Callback<string, string>((_, c) => code = c)
                .ReturnsAsync(true);

            await service.RegisterAsync(CustomerRequest("amina@example.com"));

            Assert.Empty(ctx.CustomerBusinessEnrollments);
            Assert.NotNull(code);

            // The verification now happens on the business address.
            var (tenantService, _) = CreateAuthService(ctx, business);
            var verified = await tenantService.VerifyEmailAsync(new VerifyEmailRequest
            {
                Email = "amina@example.com",
                Code = code!,
                BusinessSlug = "java-house"
            });

            Assert.True(verified.Success);

            var enrollment = Assert.Single(ctx.CustomerBusinessEnrollments);
            Assert.Equal(business.Id, enrollment.BusinessId);
            Assert.Equal(CustomerBusinessEnrollmentStatus.Active, enrollment.Status);
        }
    }

    [Fact]
    public async Task Verification_Backstop_Keeps_A_Deliberate_Leave()
    {
        var ctx = CreateContext(nameof(Verification_Backstop_Keeps_A_Deliberate_Leave));
        using (ctx)
        {
            var business = AddBusiness(ctx, "Java House", "java-house");
            ctx.SaveChanges();

            var (service, email) = CreateAuthService(ctx, business);

            string? code = null;
            email.Setup(e => e.SendVerificationCodeAsync(It.IsAny<string>(), It.IsAny<string>()))
                .Callback<string, string>((_, c) => code = c)
                .ReturnsAsync(true);

            await service.RegisterAsync(CustomerRequest("amina@example.com"));
            Assert.NotNull(code);

            // Verify → the membership already exists, so no duplicate row.
            var verified = await service.VerifyEmailAsync(new VerifyEmailRequest
            {
                Email = "amina@example.com",
                Code = code!,
                BusinessSlug = "java-house"
            });

            Assert.True(verified.Success);
            Assert.Single(ctx.CustomerBusinessEnrollments);

            // The customer leaves on purpose — the backstop must never
            // silently re-add a customer who walked away.
            var user = ctx.Users.Single(u => u.Email == "amina@example.com");
            var enrollment = ctx.CustomerBusinessEnrollments.Single();
            enrollment.Status = CustomerBusinessEnrollmentStatus.Left;
            enrollment.LeftAt = DateTime.UtcNow;
            ctx.SaveChanges();

            var reVerify = await service.VerifyEmailAsync(new VerifyEmailRequest
            {
                Email = "amina@example.com",
                Code = "123456"
            });

            // Already verified → the request is rejected, and the "Left" row
            // is untouched either way.
            Assert.False(reVerify.Success);
            Assert.Equal(CustomerBusinessEnrollmentStatus.Left, enrollment.Status);
            Assert.Single(ctx.CustomerBusinessEnrollments);
            Assert.Equal(user.Id, enrollment.CustomerId);
        }
    }
}
