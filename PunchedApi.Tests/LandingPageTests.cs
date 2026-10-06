using System.Text.Json;
using Microsoft.Data.Sqlite;
using Moq;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;
using PunchedApi.Application.Validators;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Tests;

public class LandingPageTests
{
    private sealed class Env
    {
        public required ApplicationDbContext Context { get; init; }
        public required LandingPageService Service { get; init; }
        public required Business Business { get; init; }
        public required User Owner { get; init; }
    }

    private static async Task<Env> CreateEnvAsync(SqliteConnection connection, params string[] modules)
    {
        var context = BookingTestBase.CreateContext(connection);
        var owner = BookingTestBase.CreateOwner();
        var business = BookingTestBase.CreateBusiness(owner.Id);
        await BookingTestBase.SeedAsync(context, owner, business);

        var scopeResolver = new Mock<IBusinessScopeResolver>();
        scopeResolver.Setup(x => x.GetOwnedBusinessIdAsync(owner.Id)).ReturnsAsync(business.Id);
        var service = new LandingPageService(
            context,
            scopeResolver.Object,
            new StubModuleEntitlements(modules),
            TestHelpers.CreateLogger<LandingPageService>());

        return new Env { Context = context, Service = service, Business = business, Owner = owner };
    }

    [Fact]
    public async Task GetForOwnerAsync_UsesDefaultsWhenNoConfigurationExists()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        var result = await env.Service.GetForOwnerAsync(env.Owner.Id);

        Assert.True(result.Success, result.Error?.Message);
        Assert.True(result.Data!.IsDefault);
        Assert.Equal(0, result.Data.Version);
        Assert.Equal(LandingPageDefaults.SectionKeys.Count, result.Data.Config.Sections.Count);
        Assert.All(result.Data.Config.Sections.Values, section => Assert.True(section.Enabled));
    }

    [Fact]
    public async Task UpdateForOwnerAsync_PersistsAndRejectsStaleVersion()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;
        var config = LandingPageDefaults.Create();
        config.Hero.TitleOverride = "A brighter storefront";

        var created = await env.Service.UpdateForOwnerAsync(env.Owner.Id, new UpdateLandingPageRequest
        {
            Version = 0,
            Config = config,
        });
        var stale = await env.Service.UpdateForOwnerAsync(env.Owner.Id, new UpdateLandingPageRequest
        {
            Version = 0,
            Config = config,
        });
        var loaded = await env.Service.GetForOwnerAsync(env.Owner.Id);

        Assert.True(created.Success, created.Error?.Message);
        Assert.Equal(1, created.Data!.Version);
        Assert.Equal("STALE_VERSION", stale.Error?.Code);
        Assert.False(loaded.Data!.IsDefault);
        Assert.Equal("A brighter storefront", loaded.Data.Config.Hero.TitleOverride);
    }

    [Fact]
    public async Task UpdateForOwnerAsync_RejectsHeroMediaOwnedByAnotherBusiness()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;
        var otherOwner = BookingTestBase.CreateOwner("other-owner@test.com");
        var otherBusiness = BookingTestBase.CreateBusiness(otherOwner.Id, "Other Business");
        var foreignMedia = new Media
        {
            Id = Guid.NewGuid(),
            BusinessId = otherBusiness.Id,
            UploadedByUserId = otherOwner.Id,
            Purpose = MediaPurposes.BusinessCover,
            Status = MediaStatus.Ready,
            Visibility = MediaVisibility.Public,
            SourceKey = "private/source-key",
        };
        await BookingTestBase.SeedAsync(context, otherOwner, otherBusiness, foreignMedia);
        var config = LandingPageDefaults.Create();
        config.Hero.BackgroundMediaId = foreignMedia.Id;

        var result = await env.Service.UpdateForOwnerAsync(env.Owner.Id, new UpdateLandingPageRequest
        {
            Version = 0,
            Config = config,
        });

        Assert.Equal("MEDIA_NOT_FOUND", result.Error?.Code);
        Assert.Empty(context.BusinessLandingPageConfigs);
    }

    [Fact]
    public async Task GetPublicAsync_FiltersCapabilitiesAndMediaVariants_AndTenantNarrowsBusiness()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection, "serviceCatalog", "appointments");
        using var context = env.Context;
        var publicMedia = new Media
        {
            Id = Guid.NewGuid(),
            BusinessId = env.Business.Id,
            UploadedByUserId = env.Owner.Id,
            Purpose = MediaPurposes.BusinessCover,
            Status = MediaStatus.Ready,
            Visibility = MediaVisibility.Public,
            SourceKey = "must-not-be-public",
            VariantsJson = "[{\"url\":\"https://cdn.example/hero.webp\",\"width\":1200,\"height\":600,\"format\":\"webp\"},{\"url\":\"https://cdn.example/source.png\",\"width\":2400,\"height\":1200,\"format\":\"png\"}]",
        };
        var config = LandingPageDefaults.Create();
        config.Sections["services"].Order = 1;
        await BookingTestBase.SeedAsync(context, publicMedia, new BusinessLandingPageConfig
        {
            Id = Guid.NewGuid(),
            BusinessId = env.Business.Id,
            Version = 1,
            UpdatedAt = DateTime.UtcNow,
            HeroBackgroundMediaId = publicMedia.Id,
            ConfigJson = JsonSerializer.Serialize(config, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        });

        var result = await env.Service.GetPublicAsync(env.Business.Id);
        var tenant = new TenantContext();
        tenant.Set("other", Guid.NewGuid());
        var tenantService = new LandingPageService(
            context,
            Mock.Of<IBusinessScopeResolver>(),
            new StubModuleEntitlements("serviceCatalog", "appointments"),
            TestHelpers.CreateLogger<LandingPageService>(),
            tenant);
        var narrowed = await tenantService.GetPublicAsync(env.Business.Id);
        var serialized = JsonSerializer.Serialize(result.Data, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.True(result.Success, result.Error?.Message);
        Assert.True(result.Data!.Navigation["services"]);
        Assert.False(result.Data.Navigation["loyalty"]);
        Assert.Equal("services", result.Data.Sections[0].Key);
        Assert.Single(result.Data.HeroBackgroundVariants);
        Assert.Equal("webp", result.Data.HeroBackgroundVariants[0].Format);
        Assert.DoesNotContain("ownerId", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mpesaNumber", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sourceKey", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("NOT_FOUND", narrowed.Error?.Code);
    }

    [Fact]
    public void UpdateValidator_RejectsOutOfRangeSectionOrderAndInvalidCta()
    {
        var config = LandingPageDefaults.Create();
        config.Sections["about"].Order = 51;
        config.PrimaryCta.Type = "unknown";

        var result = new UpdateLandingPageRequestValidator().Validate(new UpdateLandingPageRequest
        {
            Version = 0,
            Config = config,
        });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.PropertyName.Contains("Sections", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.PropertyName.Contains("PrimaryCta", StringComparison.Ordinal));
    }
}
