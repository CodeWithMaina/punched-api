using System.Text.Json;
using Microsoft.Data.Sqlite;
using Moq;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Tests;

/// <summary>
/// Storefront Builder: draft/published separation, publication workflow, template
/// switching, capability filtering, media ownership and tenant isolation.
/// </summary>
public class StorefrontBuilderTests
{
    private sealed class Env
    {
        public required ApplicationDbContext Context { get; init; }
        public required StorefrontBuilderService Service { get; init; }
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
        var service = new StorefrontBuilderService(
            context,
            scopeResolver.Object,
            TestHelpers.CreateLogger<StorefrontBuilderService>(),
            null,
            new StubModuleEntitlements(modules.Length == 0
                ? ["storefront", "serviceCatalog", "appointments", "gallery", "reviews", "loyalty"]
                : modules));

        return new Env { Context = context, Service = service, Business = business, Owner = owner };
    }

    private static Media CreateReadyMedia(Guid businessId, Guid ownerId, string url = "https://cdn.example/img.webp") => new()
    {
        Id = Guid.NewGuid(),
        BusinessId = businessId,
        UploadedByUserId = ownerId,
        Purpose = MediaPurposes.BusinessCover,
        Status = MediaStatus.Ready,
        Visibility = MediaVisibility.Public,
        SourceKey = "private/source",
        VariantsJson = JsonSerializer.Serialize(new[]
        {
            new { url, width = 1600, height = 900, format = "webp" },
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
    };

    [Fact]
    public void Templates_ExposeEightDistinctDesigns()
    {
        Assert.Equal(8, StorefrontTemplates.All.Count);
        // Genuinely different: every template has its own header/hero/footer trio
        // and a distinct section blueprint — not a recolour of one layout.
        var signatures = StorefrontTemplates.All
            .Select(t => $"{t.HeaderVariant}|{t.HeroVariant}|{t.FooterVariant}|{string.Join(',', t.Blueprint.Select(b => b.Key + ':' + b.Variant))}")
            .ToList();
        Assert.Equal(signatures.Count, signatures.Distinct().Count());
    }

    [Fact]
    public async Task GetForOwnerAsync_ReturnsCompleteDefault_WhenNoConfigurationExists()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        var result = await env.Service.GetForOwnerAsync(env.Owner.Id);

        Assert.True(result.Success, result.Error?.Message);
        Assert.False(result.Data!.HasDraft);
        Assert.Equal(StorefrontBuilderDefaults.DefaultTemplateId, result.Data.Draft.TemplateId);
        Assert.NotEmpty(result.Data.Draft.Sections);
        Assert.Equal(4, result.Data.Draft.Pages.Count);
    }

    [Fact]
    public async Task SaveDraft_IsNotPublished_AndDoesNotAffectPublicStorefront()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        var config = StorefrontBuilderDefaults.Create();
        config.Hero.Heading = "A brand new heading";
        var saved = await env.Service.SaveDraftAsync(env.Owner.Id, new SaveStorefrontDraftRequest { Config = config });

        var publicRead = await env.Service.GetPublishedForBusinessAsync(env.Business.Id);

        Assert.True(saved.Success, saved.Error?.Message);
        Assert.True(saved.Data!.HasDraft);
        Assert.False(saved.Data.HasUnpublishedChanges);
        Assert.Null(publicRead);
    }

    [Fact]
    public async Task Publish_MakesStorefrontPublic_AndRecordsPublication()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;
        var config = StorefrontBuilderDefaults.Create();
        config.Hero.Heading = "Welcome to the studio";
        await env.Service.SaveDraftAsync(env.Owner.Id, new SaveStorefrontDraftRequest { Config = config });

        var published = await env.Service.PublishAsync(env.Owner.Id, new PublishStorefrontRequest());
        var publicRead = await env.Service.GetPublicAsync(env.Business.Id);

        Assert.True(published.Success, published.Error?.Message);
        Assert.True(published.Data!.IsPublished);
        Assert.NotNull(published.Data.PublishedAt);
        Assert.True(publicRead.Success, publicRead.Error?.Message);
        Assert.Equal("Welcome to the studio", publicRead.Data!.Hero.Heading);
    }

    [Fact]
    public async Task SaveDraftAfterPublish_LeavesLiveStorefrontUnchanged()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;
        var live = StorefrontBuilderDefaults.Create();
        live.Hero.Heading = "Live heading";
        await env.Service.SaveDraftAsync(env.Owner.Id, new SaveStorefrontDraftRequest { Config = live });
        await env.Service.PublishAsync(env.Owner.Id, new PublishStorefrontRequest());

        var draft = StorefrontBuilderDefaults.Create();
        draft.Hero.Heading = "Draft heading (unpublished)";
        var saved = await env.Service.SaveDraftAsync(env.Owner.Id, new SaveStorefrontDraftRequest { Config = draft });
        var publicRead = await env.Service.GetPublishedForBusinessAsync(env.Business.Id);

        Assert.True(saved.Data!.HasUnpublishedChanges);
        Assert.Equal("Live heading", publicRead!.Hero.Heading);
    }

    [Fact]
    public async Task Unpublish_HidesStorefront_WithoutDeletingConfiguration()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;
        await env.Service.SaveDraftAsync(env.Owner.Id, new SaveStorefrontDraftRequest { Config = StorefrontBuilderDefaults.Create() });
        await env.Service.PublishAsync(env.Owner.Id, new PublishStorefrontRequest());

        var unpublished = await env.Service.UnpublishAsync(env.Owner.Id);
        var publicRead = await env.Service.GetPublishedForBusinessAsync(env.Business.Id);

        Assert.True(unpublished.Success, unpublished.Error?.Message);
        Assert.False(unpublished.Data!.IsPublished);
        Assert.Null(publicRead);
    }

    [Fact]
    public async Task ResetDraft_RestoresPublishedConfiguration()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;
        var live = StorefrontBuilderDefaults.Create();
        live.Hero.Heading = "Published";
        await env.Service.SaveDraftAsync(env.Owner.Id, new SaveStorefrontDraftRequest { Config = live });
        await env.Service.PublishAsync(env.Owner.Id, new PublishStorefrontRequest());

        var draft = StorefrontBuilderDefaults.Create();
        draft.Hero.Heading = "Scratch";
        await env.Service.SaveDraftAsync(env.Owner.Id, new SaveStorefrontDraftRequest { Config = draft });
        var reset = await env.Service.ResetDraftAsync(env.Owner.Id);

        Assert.True(reset.Success, reset.Error?.Message);
        Assert.Equal("Published", reset.Data!.Draft.Hero.Heading);
        Assert.False(reset.Data.HasUnpublishedChanges);
    }

    [Fact]
    public async Task ApplyTemplate_ChangesDesign_PreservesOwnerContent()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        var config = StorefrontBuilderDefaults.Create();
        config.Sections[0].Title = "My services heading";
        await env.Service.SaveDraftAsync(env.Owner.Id, new SaveStorefrontDraftRequest { Config = config });

        var applied = await env.Service.ApplyTemplateAsync(env.Owner.Id, "luxe");

        Assert.True(applied.Success, applied.Error?.Message);
        Assert.Equal("luxe", applied.Data!.Draft.TemplateId);
        Assert.Equal("floating", applied.Data.Draft.Header.Variant);
        Assert.Equal("premium", applied.Data.Draft.Hero.Variant);
        // Owner content survives the design switch.
        Assert.Equal("My services heading", applied.Data.Draft.Sections.First(s => s.Key == "services").Title);
    }

    [Fact]
    public async Task ApplyTemplate_RejectsUnknownTemplate()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        var applied = await env.Service.ApplyTemplateAsync(env.Owner.Id, "not-a-template");

        Assert.Equal("VALIDATION_ERROR", applied.Error?.Code);
    }

    [Fact]
    public async Task GetPublicAsync_FiltersSectionsByEntitlement()
    {
        using var connection = BookingTestBase.CreateConnection();
        // Only services + appointments entitled: gallery / reviews sections drop out.
        var env = await CreateEnvAsync(connection, "storefront", "serviceCatalog", "appointments");
        using var context = env.Context;

        var config = StorefrontBuilderDefaults.Create();
        config.Sections.Add(new StorefrontSectionDto { Id = "g1", Key = "gallery", Variant = "grid", Order = 900 });
        config.Sections.Add(new StorefrontSectionDto { Id = "t1", Key = "testimonials", Variant = "cards", Order = 910 });
        await env.Service.SaveDraftAsync(env.Owner.Id, new SaveStorefrontDraftRequest { Config = config });
        await env.Service.PublishAsync(env.Owner.Id, new PublishStorefrontRequest());

        var publicRead = await env.Service.GetPublicAsync(env.Business.Id);

        Assert.True(publicRead.Success, publicRead.Error?.Message);
        Assert.Contains(publicRead.Data!.Sections, s => s.Key == "services");
        Assert.DoesNotContain(publicRead.Data.Sections, s => s.Key == "gallery");
        Assert.DoesNotContain(publicRead.Data.Sections, s => s.Key == "testimonials");
    }

    [Fact]
    public async Task GetPublishedForBusinessAsync_NeverReturnsAnotherTenantsStorefront()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;
        await env.Service.SaveDraftAsync(env.Owner.Id, new SaveStorefrontDraftRequest { Config = StorefrontBuilderDefaults.Create() });
        await env.Service.PublishAsync(env.Owner.Id, new PublishStorefrontRequest());

        var otherOwner = BookingTestBase.CreateOwner("other-owner@test.com");
        var otherBusiness = BookingTestBase.CreateBusiness(otherOwner.Id, "Other Business");
        await BookingTestBase.SeedAsync(context, otherOwner, otherBusiness);

        var tenant = new TenantContext();
        tenant.Set("test-business", env.Business.Id);
        var narrowed = new StorefrontBuilderService(
            context,
            Mock.Of<IBusinessScopeResolver>(),
            TestHelpers.CreateLogger<StorefrontBuilderService>(),
            tenant,
            new StubModuleEntitlements("storefront", "serviceCatalog"));

        var ownRead = await narrowed.GetPublishedForBusinessAsync(env.Business.Id);
        var otherRead = await narrowed.GetPublishedForBusinessAsync(otherBusiness.Id);

        Assert.NotNull(ownRead);
        Assert.Null(otherRead);
    }

    [Fact]
    public async Task SaveDraft_RejectsMediaOwnedByAnotherBusiness()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;
        var otherOwner = BookingTestBase.CreateOwner("other-owner@test.com");
        var otherBusiness = BookingTestBase.CreateBusiness(otherOwner.Id, "Other Business");
        var foreignMedia = CreateReadyMedia(otherBusiness.Id, otherOwner.Id);
        await BookingTestBase.SeedAsync(context, otherOwner, otherBusiness, foreignMedia);

        var config = StorefrontBuilderDefaults.Create();
        config.Hero.BackgroundMediaId = foreignMedia.Id;
        var result = await env.Service.SaveDraftAsync(env.Owner.Id, new SaveStorefrontDraftRequest { Config = config });

        Assert.Equal("MEDIA_NOT_FOUND", result.Error?.Code);
        Assert.Empty(context.BusinessLandingPageConfigs);
    }

    [Fact]
    public async Task GetPublicAsync_ResolvesOwnedMediaVariants()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;
        var media = CreateReadyMedia(env.Business.Id, env.Owner.Id, "https://cdn.example/hero.webp");
        await BookingTestBase.SeedAsync(context, media);

        var config = StorefrontBuilderDefaults.Create();
        config.Hero.BackgroundMediaId = media.Id;
        await env.Service.SaveDraftAsync(env.Owner.Id, new SaveStorefrontDraftRequest { Config = config });
        await env.Service.PublishAsync(env.Owner.Id, new PublishStorefrontRequest());

        var publicRead = await env.Service.GetPublicAsync(env.Business.Id);

        Assert.True(publicRead.Success, publicRead.Error?.Message);
        var variant = Assert.Single(publicRead.Data!.HeroBackgroundVariants);
        Assert.Equal("https://cdn.example/hero.webp", variant.Url);
    }
}
