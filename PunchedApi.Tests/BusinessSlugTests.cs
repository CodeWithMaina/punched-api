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
/// Business subdomain slugs: normalization, reserved names, collision
/// suffixing, owner edits with redirect history, tenant resolution and the
/// startup backfill — the backend half of java-house.punched.app.
/// </summary>
public class BusinessSlugTests
{
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

    private static BusinessSlugGenerator CreateGenerator(ApplicationDbContext context) =>
        new(context);

    private static BusinessService CreateBusinessService(ApplicationDbContext context, IModuleEntitlementService? moduleEntitlements = null) =>
        new(
            new UnitOfWork(context),
            context,
            new Mock<IInsightService>().Object,
            new Mock<IBusinessScopeResolver>().Object,
            new Mock<ISubscriptionProvisioningService>().Object,
            moduleEntitlements ?? new Mock<IModuleEntitlementService>().Object,
            CreateGenerator(context),
            TestHelpers.CreateLogger<BusinessService>());

    private static AuthService CreateAuthService(ApplicationDbContext context)
    {
        var emailMock = new Mock<IEmailService>();
        emailMock.Setup(e => e.SendVerificationCodeAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        return new AuthService(
            new UnitOfWork(context),
            CreateJwtService(),
            emailMock.Object,
            CreateMapper(),
            TestHelpers.CreateLogger<AuthService>(),
            new Mock<ISubscriptionProvisioningService>().Object,
            CreateGenerator(context),
            // Tenant-aware auth: real membership view over Owner/Staff/Enrollment;
            // host resolution is never exercised without a businessSlug here.
            new BusinessMembershipResolver(context),
            new Mock<ITenantHostResolver>().Object);
    }

    private static User AddOwner(ApplicationDbContext context, string email = "owner@t.com")
    {
        var owner = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            FullName = "Owner",
            Role = UserRole.Business,
            CreatedAt = DateTime.UtcNow
        };
        context.Users.Add(owner);
        context.SaveChanges();
        return owner;
    }

    private static Business AddBusiness(ApplicationDbContext context, User owner, string name, string? slug)
    {
        var business = new Business
        {
            Id = Guid.NewGuid(),
            Name = name,
            Category = "Cafe",
            Location = "Nairobi",
            MpesaNumber = "1",
            OwnerId = owner.Id,
            Slug = slug,
            CreatedAt = DateTime.UtcNow
        };
        context.Businesses.Add(business);
        context.SaveChanges();
        return business;
    }

    // ── Normalization + format rules (pure policy) ─────────────────────

    [Theory]
    [InlineData("Java House", "java-house")]
    [InlineData("  Chege's Java Hut!  ", "chege-s-java-hut")]
    [InlineData("Café MÜNCHEN", "cafe-munchen")]
    [InlineData("Mama & Papa's Diner", "mama-papa-s-diner")]
    [InlineData("---Hi---", "hi")]
    public void Slugify_NormalizesBusinessNames(string name, string expected) =>
        Assert.Equal(expected, BusinessSlugPolicy.Slugify(name));

    [Theory]
    [InlineData("")]
    [InlineData("🍕🍕")]
    [InlineData("!!! ???")]
    public void Slugify_FallsBackToBusiness_WhenNothingUsableRemains(string name) =>
        Assert.Equal("business", BusinessSlugPolicy.Slugify(name));

    [Fact]
    public void Slugify_Enforces63CharDnsLabelLimit()
    {
        var slug = BusinessSlugPolicy.Slugify(new string('a', 100));
        Assert.Equal(63, slug.Length);

        // Truncation must not leave a trailing hyphen.
        var hyphenated = BusinessSlugPolicy.Slugify(new string('a', 62) + " tail");
        Assert.DoesNotContain('-', hyphenated[^1].ToString());
        Assert.True(hyphenated.Length <= 63);
    }

    [Theory]
    [InlineData("java-house", true)]
    [InlineData("a", true)]
    [InlineData("x-2y12", true)]
    [InlineData("", false)]
    [InlineData("-java", false)]
    [InlineData("java-", false)]
    [InlineData("Java", false)]
    [InlineData("ja va", false)]
    public void IsValidFormat_EnforcesCharsetLengthAndEdges(string slug, bool expected) =>
        Assert.Equal(expected, BusinessSlugPolicy.IsValidFormat(slug));

    [Fact]
    public async Task Generate_UsesNormalizedNameBase()
    {
        using var context = CreateContext("Slug_Gen_Base");
        var generator = CreateGenerator(context);

        Assert.Equal("java-house", await generator.GenerateAsync("Java House"));
    }

    [Fact]
    public async Task Generate_AppendsNumberedSuffix_OnCollision()
    {
        using var context = CreateContext("Slug_Gen_Collision");
        var owner = AddOwner(context);
        AddBusiness(context, owner, "Java House", "java-house");
        var generator = CreateGenerator(context);

        Assert.Equal("java-house-2", await generator.GenerateAsync("Java House"));

        // Persist the -2 candidate, then generation must move on to -3.
        AddBusiness(context, AddOwner(context, "b@t.com"), "Java House 2", "java-house-2");
        Assert.Equal("java-house-3", await generator.GenerateAsync("Java House"));
    }

    [Fact]
    public async Task Generate_NeverAssignsReservedNames()
    {
        using var context = CreateContext("Slug_Gen_Reserved");
        var generator = CreateGenerator(context);

        var slug = await generator.GenerateAsync("Api");

        Assert.Equal("api-2", slug);
        Assert.False(BusinessSlugPolicy.IsReserved(slug));
    }

    [Fact]
    public async Task Generate_SkipsAddressesHeldInHistory_ByOtherBusinesses()
    {
        using var context = CreateContext("Slug_Gen_History");
        var owner = AddOwner(context);
        var other = AddBusiness(context, owner, "Old Address", "new-address");
        context.BusinessSlugHistories.Add(new BusinessSlugHistory
        {
            Id = Guid.NewGuid(),
            Slug = "old-address",
            BusinessId = other.Id,
            CreatedAt = DateTime.UtcNow
        });
        context.SaveChanges();
        var generator = CreateGenerator(context);

        // "old-address" still redirects to the other business — can't capture it.
        Assert.Equal("old-address-2", await generator.GenerateAsync("Old Address"));
    }

    [Theory]
    [InlineData("  Java-House ", true, "java-house")]
    [InlineData("java house", false, "SLUG_INVALID")]
    [InlineData("-java", false, "SLUG_INVALID")]
    [InlineData("admin", false, "SLUG_RESERVED")]
    [InlineData("", false, "SLUG_INVALID")]
    public void Validate_ChecksFormatAndReservedNames(string input, bool expectOk, string expectedCodeOrSlug)
    {
        var (ok, slug, code, _) = CreateGenerator(CreateContext("Slug_Validate_" + Guid.NewGuid()))
            .Validate(input);

        Assert.Equal(expectOk, ok);
        if (expectOk) Assert.Equal(expectedCodeOrSlug, slug);
        else Assert.Equal(expectedCodeOrSlug, code);
    }

    // ── Service flows (create / edit / resolve / backfill) ─────────────

    [Fact]
    public async Task RegisterBusiness_AssignsGeneratedSlug()
    {
        using var context = CreateContext("Slug_Register");
        var service = CreateAuthService(context);

        var result = await service.RegisterBusinessAsync(new RegisterBusinessRequest
        {
            FullName = "Jane Chege",
            Email = "owner@example.com",
            Password = "P@ssw0rd!12",
            PhoneNumber = "+254700000000",
            BusinessName = "Chege's Java Hut",
            BusinessCategory = "Cafe",
            BusinessLocation = "Nairobi",
            BusinessPhone = "+254700000001",
            BusinessEmail = "cafe@example.com",
            BusinessMpesaNumber = "123456",
            BusinessDescription = "Specialty coffee and pastries."
        });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("chege-s-java-hut", result.Data!.Business!.Slug);

        var persisted = await context.Businesses.SingleAsync(b => b.Name == "Chege's Java Hut");
        Assert.Equal("chege-s-java-hut", persisted.Slug);
    }

    [Fact]
    public async Task CreateBusiness_AssignsGeneratedSlug()
    {
        using var context = CreateContext("Slug_Create");
        var owner = AddOwner(context, "newowner@t.com");
        var service = CreateBusinessService(context);

        var result = await service.CreateBusinessAsync(owner.Id, new CreateBusinessRequest
        {
            Name = "Java House",
            Category = "Cafe",
            Location = "Nairobi",
            MpesaNumber = "1"
        });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("java-house", result.Data!.Slug);
    }

    [Fact]
    public async Task UpdateSlug_ChangesSlug_AndArchivesTheOldOne()
    {
        using var context = CreateContext("Slug_Update");
        var owner = AddOwner(context);
        var business = AddBusiness(context, owner, "Java House", "java-house");
        var service = CreateBusinessService(context);

        var result = await service.UpdateMyBusinessSlugAsync(owner.Id,
            new UpdateBusinessSlugRequest { Slug = "jh-coffee" });

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("jh-coffee", result.Data!.Slug);

        var reloaded = await context.Businesses.SingleAsync(b => b.Id == business.Id);
        Assert.Equal("jh-coffee", reloaded.Slug);

        var history = await context.BusinessSlugHistories.SingleAsync(h => h.BusinessId == business.Id);
        Assert.Equal("java-house", history.Slug);
    }

    [Fact]
    public async Task UpdateSlug_OldAddressResolvesAsMoved_ToCanonicalSlug()
    {
        using var context = CreateContext("Slug_ResolveMoved");
        var owner = AddOwner(context);
        var business = AddBusiness(context, owner, "Java House", "java-house");
        var service = CreateBusinessService(context);

        var changed = await service.UpdateMyBusinessSlugAsync(owner.Id,
            new UpdateBusinessSlugRequest { Slug = "jh-coffee" });
        Assert.True(changed.Success, changed.Error?.Message);

        var oldAddress = await service.ResolveBySlugAsync("java-house");
        Assert.True(oldAddress.Success);
        Assert.True(oldAddress.Data!.Moved);
        Assert.Equal("jh-coffee", oldAddress.Data.Slug);
        Assert.Equal(business.Id, oldAddress.Data.BusinessId);

        var current = await service.ResolveBySlugAsync("jh-coffee");
        Assert.True(current.Success);
        Assert.False(current.Data!.Moved);
        Assert.Equal(business.Id, current.Data.BusinessId);

        var garbage = await service.ResolveBySlugAsync("bad slug!");
        Assert.False(garbage.Success);
        Assert.Equal("NOT_FOUND", garbage.Error!.Code);
    }

    [Fact]
    public async Task UpdateSlug_RejectsTakenReservedAndInvalidValues()
    {
        using var context = CreateContext("Slug_UpdateRejects");
        var owner = AddOwner(context);
        AddBusiness(context, owner, "Mine", "my-shop");
        AddBusiness(context, AddOwner(context, "other@t.com"), "Theirs", "taken-slug");
        var service = CreateBusinessService(context);

        var taken = await service.UpdateMyBusinessSlugAsync(owner.Id,
            new UpdateBusinessSlugRequest { Slug = "taken-slug" });
        Assert.False(taken.Success);
        Assert.Equal("SLUG_TAKEN", taken.Error!.Code);

        var reserved = await service.UpdateMyBusinessSlugAsync(owner.Id,
            new UpdateBusinessSlugRequest { Slug = "admin" });
        Assert.False(reserved.Success);
        Assert.Equal("SLUG_RESERVED", reserved.Error!.Code);

        var invalid = await service.UpdateMyBusinessSlugAsync(owner.Id,
            new UpdateBusinessSlugRequest { Slug = "not a slug" });
        Assert.False(invalid.Success);
        Assert.Equal("SLUG_INVALID", invalid.Error!.Code);
    }

    [Fact]
    public async Task UpdateSlug_AllowsReclaimingOwnFormerSlug_AndDropsItsHistory()
    {
        using var context = CreateContext("Slug_Reclaim");
        var owner = AddOwner(context);
        AddBusiness(context, owner, "Java House", "java-house");
        var service = CreateBusinessService(context);

        var moved = await service.UpdateMyBusinessSlugAsync(owner.Id,
            new UpdateBusinessSlugRequest { Slug = "jh-coffee" });
        Assert.True(moved.Success, moved.Error?.Message);

        var back = await service.UpdateMyBusinessSlugAsync(owner.Id,
            new UpdateBusinessSlugRequest { Slug = "java-house" });
        Assert.True(back.Success, back.Error?.Message);
        Assert.Equal("java-house", back.Data!.Slug);

        // The reclaimed address's history row is dropped (no self-redirect),
        // while the row we just abandoned (jh-coffee) keeps redirecting here.
        var historyRows = await context.BusinessSlugHistories.ToListAsync();
        Assert.Single(historyRows);
        Assert.Equal("jh-coffee", historyRows[0].Slug);

        // The reclaimed address is canonical again — no self-redirect.
        var resolved = await service.ResolveBySlugAsync("java-house");
        Assert.True(resolved.Success);
        Assert.False(resolved.Data!.Moved);

        var abandoned = await service.ResolveBySlugAsync("jh-coffee");
        Assert.True(abandoned.Success);
        Assert.True(abandoned.Data!.Moved);
        Assert.Equal("java-house", abandoned.Data.Slug);
    }

    [Fact]
    public async Task Backfill_AssignsUniqueSlugsToLegacyBusinesses()
    {
        using var context = CreateContext("Slug_Backfill");
        var owner = AddOwner(context);
        AddBusiness(context, owner, "Java House", slug: null);
        AddBusiness(context, AddOwner(context, "b@t.com"), "Java House", slug: "");
        var backfill = new BusinessSlugBackfill(
            context,
            CreateGenerator(context),
            TestHelpers.CreateLogger<BusinessSlugBackfill>());

        var assigned = await backfill.EnsureSlugsAsync();

        Assert.Equal(2, assigned);
        var slugs = (await context.Businesses.Select(b => b.Slug).ToListAsync())!;
        Assert.All(slugs, s => Assert.False(string.IsNullOrEmpty(s)));
        Assert.Equal(slugs.Count, slugs.Distinct().Count());
        Assert.Contains("java-house", slugs);
        Assert.Contains("java-house-2", slugs);

        // Idempotent: a second run finds nothing to fix.
        Assert.Equal(0, await backfill.EnsureSlugsAsync());
    }

    [Fact]
    public async Task PublicProfile_EmbedsOnlyReadyPublicLogoVariants()
    {
        using var context = CreateContext("PublicProfile_LogoVariants");
        var owner = AddOwner(context);
        var business = AddBusiness(context, owner, "Logo Test", "logo-test");
        var image = new Media
        {
            Id = Guid.NewGuid(), BusinessId = business.Id, UploadedByUserId = owner.Id,
            Purpose = MediaPurposes.BusinessLogo, SourceKey = "private/source/logo", Status = MediaStatus.Ready,
            Visibility = MediaVisibility.Public,
            VariantsJson = "[{\"url\":\"https://cdn.example/320.webp\",\"width\":320,\"height\":320,\"format\":\"webp\"}]"
        };
        business.LogoMediaId = image.Id;
        context.Media.Add(image);
        await context.SaveChangesAsync();
        var moduleEntitlements = new Mock<IModuleEntitlementService>();
        moduleEntitlements.Setup(service => service.GetEffectiveModuleKeysAsync(business.Id))
            .ReturnsAsync(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        var response = await CreateBusinessService(context, moduleEntitlements.Object).GetPublicProfileAsync(business.Id);

        Assert.True(response.Success, response.Error?.Message);
        Assert.Equal("https://cdn.example/320.webp", Assert.Single(response.Data!.LogoVariants).Url);
    }
}