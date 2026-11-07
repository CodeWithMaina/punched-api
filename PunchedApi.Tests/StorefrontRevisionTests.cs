using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Tests;

/// <summary>
/// Storefront revision history: when revisions are created (and when they are
/// deliberately not), snapshot immutability, rollback semantics, comparison,
/// tenant isolation and the concurrency guards.
/// </summary>
public class StorefrontRevisionTests
{
    private sealed class Env
    {
        public required ApplicationDbContext Context { get; init; }
        public required StorefrontBuilderService Builder { get; init; }
        public required StorefrontRevisionService Revisions { get; init; }
        public required Business Business { get; init; }
        public required User Owner { get; init; }
        public required Business OtherBusiness { get; init; }
        public required User OtherOwner { get; init; }
        public required StorefrontRevisionService OtherRevisions { get; init; }
        public required StorefrontBuilderService OtherBuilder { get; init; }
    }

    private static (StorefrontBuilderService Builder, StorefrontRevisionService Revisions) ServicesFor(
        ApplicationDbContext context, Guid ownerId, Guid businessId)
    {
        var scope = new Mock<IBusinessScopeResolver>();
        scope.Setup(x => x.GetOwnedBusinessIdAsync(ownerId)).ReturnsAsync(businessId);

        var modules = new StubModuleEntitlements(
            "storefront", "serviceCatalog", "appointments", "gallery", "reviews", "loyalty");

        return (
            new StorefrontBuilderService(
                context, scope.Object, TestHelpers.CreateLogger<StorefrontBuilderService>(), null, modules),
            new StorefrontRevisionService(
                context, scope.Object, TestHelpers.CreateLogger<StorefrontRevisionService>(), null));
    }

    private static async Task<Env> CreateEnvAsync(SqliteConnection connection)
    {
        var context = BookingTestBase.CreateContext(connection);

        var owner = BookingTestBase.CreateOwner();
        var business = BookingTestBase.CreateBusiness(owner.Id, "Revision Salon");
        var otherOwner = BookingTestBase.CreateOwner("revision-other@test.com");
        var otherBusiness = BookingTestBase.CreateBusiness(otherOwner.Id, "Other Salon");

        await BookingTestBase.SeedAsync(context, owner, business);
        await BookingTestBase.SeedAsync(context, otherOwner, otherBusiness);

        var mine = ServicesFor(context, owner.Id, business.Id);
        var theirs = ServicesFor(context, otherOwner.Id, otherBusiness.Id);

        return new Env
        {
            Context = context,
            Builder = mine.Builder,
            Revisions = mine.Revisions,
            Business = business,
            Owner = owner,
            OtherBusiness = otherBusiness,
            OtherOwner = otherOwner,
            OtherRevisions = theirs.Revisions,
            OtherBuilder = theirs.Builder,
        };
    }

    /// <summary>The default storefront, optionally with a changed hero and an extra section.</summary>
    private static StorefrontConfigDto Config(string? heading = null, string? addSectionKey = null)
    {
        var config = StorefrontBuilderDefaults.Create();
        if (heading != null) config.Hero.Heading = heading;
        if (addSectionKey != null)
        {
            var maxOrder = config.Sections.Count == 0 ? 0 : config.Sections.Max(s => s.Order);
            config.Sections.Add(new StorefrontSectionDto
            {
                Id = "sec-" + addSectionKey,
                Key = addSectionKey,
                Variant = StorefrontSectionCatalog.NormaliseVariant(addSectionKey, null),
                Enabled = true,
                Order = maxOrder + 10,
                Title = addSectionKey + " heading",
                Description = null,
                Settings = new Dictionary<string, string>(),
            });
        }
        return config;
    }

    private static Task<ApiResponse<StorefrontBuilderStateResponse>> Save(
        Env env, StorefrontConfigDto config, int? expectedVersion = null)
        => env.Builder.SaveDraftAsync(env.Owner.Id, new SaveStorefrontDraftRequest
        {
            Config = config,
            ExpectedVersion = expectedVersion,
        });

    private static Task<ApiResponse<StorefrontRevisionListResponse>> List(
        Env env, int page = 1, int pageSize = 20, string? type = null, bool publishedOnly = false)
        => env.Revisions.ListAsync(env.Owner.Id, new StorefrontRevisionQuery
        {
            Page = page,
            PageSize = pageSize,
            Type = type,
            PublishedOnly = publishedOnly,
        });

    private static async Task<List<StorefrontRevisionSummaryResponse>> History(Env env)
    {
        var result = await List(env, pageSize: 100);
        Assert.True(result.Success, result.Error?.Message);
        return result.Data!.Items;
    }

    private static async Task<StorefrontConfigDto> DraftAsync(Env env)
    {
        var state = await env.Builder.GetForOwnerAsync(env.Owner.Id);
        Assert.True(state.Success, state.Error?.Message);
        return state.Data!.Draft;
    }

    private static Media ReadyMedia(Guid businessId, Guid ownerId) => new()
    {
        Id = Guid.NewGuid(),
        BusinessId = businessId,
        UploadedByUserId = ownerId,
        Purpose = MediaPurposes.BusinessCover,
        Status = MediaStatus.Ready,
        Visibility = MediaVisibility.Public,
        SourceKey = "private/source",
        VariantsJson = "[]",
    };

    [Fact]
    public async Task MeaningfulDraftSave_CreatesAnImmutableRevision()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        var first = await Save(env, Config("First heading"));
        Assert.True(first.Success, first.Error?.Message);

        // The very first save establishes the baseline, so history is never empty
        // for a storefront that predates this feature.
        var baseline = Assert.Single(await History(env));
        Assert.Equal(1, baseline.RevisionNumber);
        Assert.Equal("draftSave", baseline.RevisionType);
        Assert.Equal("Baseline snapshot", baseline.Description);
        Assert.Equal(env.Owner.Id, baseline.CreatedByUserId);
        Assert.Equal("Test Owner", baseline.CreatedByName);
        Assert.False(baseline.IsPublishedSnapshot);

        await Save(env, Config("Second heading"));

        var history = await History(env);
        Assert.Equal(2, history.Count);
        Assert.Equal(2, history[0].RevisionNumber);
        Assert.Equal("Draft saved", history[0].Description);
    }

    [Fact]
    public async Task UnchangedSave_CreatesNoRevisionAndDoesNotMoveTheVersion()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        var config = Config("Steady");
        var first = await Save(env, config);
        var version = first.Data!.DraftVersion;

        var second = await Save(env, config);

        Assert.Equal(version, second.Data!.DraftVersion);
        Assert.Single(await History(env));
    }

    [Fact]
    public async Task SequentialSaves_NumberRevisionsInOrder()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        await Save(env, Config("One"));
        await Save(env, Config("Two"));
        await Save(env, Config("Three"));

        Assert.Equal(new[] { 3, 2, 1 }, (await History(env)).Select(r => r.RevisionNumber));

        var stored = await context.StorefrontRevisions.AsNoTracking()
            .Select(r => r.RevisionNumber)
            .ToListAsync();
        Assert.Equal(new[] { 1, 2, 3 }, stored.OrderBy(n => n));
    }

    [Fact]
    public async Task ARevisionIsAnIndependentSnapshot_LaterEditsDoNotChangeIt()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        await Save(env, Config("Original heading"));
        await Save(env, Config("Replacement heading"));

        var first = (await History(env)).Single(r => r.RevisionNumber == 1);
        var detail = await env.Revisions.GetAsync(env.Owner.Id, first.Id);

        Assert.True(detail.Success, detail.Error?.Message);
        Assert.Equal("Original heading", detail.Data!.Configuration.Hero.Heading);

        // The stored payload itself is untouched by the later save.
        var storedJson = await context.StorefrontRevisions.AsNoTracking()
            .Where(r => r.Id == first.Id)
            .Select(r => r.ConfigurationJson)
            .FirstAsync();
        Assert.Contains("Original heading", storedJson);

        Assert.Equal("Replacement heading", (await DraftAsync(env)).Hero.Heading);
    }

    [Fact]
    public async Task Publish_RecordsAPublishedSnapshot()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        await Save(env, Config("Hero for publish"));
        var published = await env.Builder.PublishAsync(env.Owner.Id, new PublishStorefrontRequest());
        Assert.True(published.Success, published.Error?.Message);

        var top = (await History(env)).First();
        Assert.Equal("publish", top.RevisionType);
        Assert.True(top.IsPublishedSnapshot);
        Assert.Equal(env.Owner.Id, top.CreatedByUserId);

        Assert.True(published.Data!.IsPublished);
        Assert.NotNull(published.Data.PublishedAt);
        Assert.Equal("Hero for publish", published.Data.Published!.Hero.Heading);

        // Republishing the payload that is already live adds no revision.
        await env.Builder.PublishAsync(env.Owner.Id, new PublishStorefrontRequest());
        Assert.Equal(2, (await History(env)).Count);
    }

    [Fact]
    public async Task FailedPublish_CreatesNoRevisionAndLeavesTheLiveStorefrontAlone()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        await Save(env, Config("Safe hero"));

        var foreignMedia = ReadyMedia(env.OtherBusiness.Id, env.OtherOwner.Id);
        await BookingTestBase.SeedAsync(context, foreignMedia);

        var broken = Config("Broken hero");
        broken.Hero.BackgroundMediaId = foreignMedia.Id;
        var publish = await env.Builder.PublishAsync(env.Owner.Id, new PublishStorefrontRequest { Config = broken });

        Assert.Equal("MEDIA_NOT_FOUND", publish.Error?.Code);

        var history = await History(env);
        Assert.Single(history);
        Assert.DoesNotContain(history, r => r.RevisionType == "publish");

        var row = await context.BusinessLandingPageConfigs.AsNoTracking().FirstAsync();
        Assert.Null(row.PublishedConfigJson);
        Assert.Null(row.PublishedAt);
    }

    [Fact]
    public async Task Restore_CreatesARollbackRevisionAndRestoresTheDraft()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        await Save(env, Config("Original heading"));
        await Save(env, Config("Changed heading"));
        var version = (await env.Builder.GetForOwnerAsync(env.Owner.Id)).Data!.DraftVersion;

        var first = (await History(env)).Single(r => r.RevisionNumber == 1);
        var restore = await env.Revisions.RestoreAsync(env.Owner.Id, first.Id,
            new RestoreStorefrontRevisionRequest { Confirm = true, ExpectedVersion = version });

        Assert.True(restore.Success, restore.Error?.Message);
        Assert.Equal("rollback", restore.Data!.RevisionType);
        Assert.Equal(first.Id, restore.Data.SourceRevisionId);
        Assert.Equal(1, restore.Data.SourceRevisionNumber);
        Assert.Equal(3, restore.Data.RevisionNumber);
        Assert.Equal("Restored version #1", restore.Data.Description);
        Assert.Equal(env.Owner.Id, restore.Data.CreatedByUserId);

        Assert.Equal("Original heading", (await DraftAsync(env)).Hero.Heading);
        Assert.Equal(3, (await History(env)).Count);
    }

    [Fact]
    public async Task Rollback_LeavesEveryEarlierRevisionIntact()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        await Save(env, Config("Original heading"));
        await Save(env, Config("Changed heading"));
        var version = (await env.Builder.GetForOwnerAsync(env.Owner.Id)).Data!.DraftVersion;
        var first = (await History(env)).Single(r => r.RevisionNumber == 1);

        await env.Revisions.RestoreAsync(env.Owner.Id, first.Id,
            new RestoreStorefrontRevisionRequest { Confirm = true, ExpectedVersion = version });

        var stored = await context.StorefrontRevisions.AsNoTracking()
            .OrderBy(r => r.RevisionNumber)
            .Select(r => new { r.RevisionNumber, r.ConfigurationJson })
            .ToListAsync();

        Assert.Contains("Original heading", stored.Single(r => r.RevisionNumber == 1).ConfigurationJson);
        Assert.Contains("Changed heading", stored.Single(r => r.RevisionNumber == 2).ConfigurationJson);
    }

    [Fact]
    public async Task Rollback_DoesNotChangeThePublishedStorefront()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        await Save(env, Config("Live heading"));
        await env.Builder.PublishAsync(env.Owner.Id, new PublishStorefrontRequest());
        await Save(env, Config("Second published heading"));
        await env.Builder.PublishAsync(env.Owner.Id, new PublishStorefrontRequest());

        // The first revision holds the *older* live configuration.
        var first = (await History(env)).Single(r => r.RevisionNumber == 1);
        var version = (await env.Builder.GetForOwnerAsync(env.Owner.Id)).Data!.DraftVersion;

        var restore = await env.Revisions.RestoreAsync(env.Owner.Id, first.Id,
            new RestoreStorefrontRevisionRequest { Confirm = true, ExpectedVersion = version });
        Assert.True(restore.Success, restore.Error?.Message);

        // The draft went back; the live storefront did not.
        Assert.Equal("Live heading", (await DraftAsync(env)).Hero.Heading);

        var row = await context.BusinessLandingPageConfigs.AsNoTracking().FirstAsync();
        Assert.Contains("Second published heading", row.PublishedConfigJson!);
        Assert.True(row.IsPublished);

        var publicRead = await env.Builder.GetPublicAsync(env.Business.Id);
        Assert.True(publicRead.Success, publicRead.Error?.Message);
        Assert.Equal("Second published heading", publicRead.Data!.Hero.Heading);
    }

    [Fact]
    public async Task Reset_CreatesARevisionWhenTheDraftChanges()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        await Save(env, Config("Live heading"));
        await env.Builder.PublishAsync(env.Owner.Id, new PublishStorefrontRequest());
        await Save(env, Config("Unpublished experiment"));

        var reset = await env.Builder.ResetDraftAsync(env.Owner.Id);
        Assert.True(reset.Success, reset.Error?.Message);

        var top = (await History(env)).First();
        Assert.Equal("reset", top.RevisionType);
        Assert.Equal("Draft reset to the published version", top.Description);

        Assert.Equal("Live heading", (await DraftAsync(env)).Hero.Heading);
        Assert.Equal(4, (await History(env)).Count);
    }

    [Fact]
    public async Task NoOpReset_CreatesNoRevision()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        await Save(env, Config("Live heading"));
        await env.Builder.PublishAsync(env.Owner.Id, new PublishStorefrontRequest());
        var before = (await History(env)).Count;

        // The draft already equals the published configuration, so resetting it
        // discards nothing and must not fill history with a no-op.
        var reset = await env.Builder.ResetDraftAsync(env.Owner.Id);

        Assert.True(reset.Success, reset.Error?.Message);
        Assert.Equal(before, (await History(env)).Count);
        Assert.DoesNotContain(await History(env), r => r.RevisionType == "reset");
    }

    [Fact]
    public async Task Compare_ReportsAddedAndRemovedSections()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        var withoutGallery = Config("Hero heading");
        withoutGallery.Sections = withoutGallery.Sections.Where(s => s.Key != "gallery").ToList();
        await Save(env, withoutGallery);
        await Save(env, Config("Hero heading"));

        var history = await History(env);
        var rev1 = history.Single(r => r.RevisionNumber == 1);
        var rev2 = history.Single(r => r.RevisionNumber == 2);

        var added = await env.Revisions.CompareAsync(env.Owner.Id, rev1.Id, rev2.Id);
        Assert.True(added.Success, added.Error?.Message);
        Assert.False(added.Data!.Identical);
        Assert.Contains(added.Data.Changes, c => c.Kind == "added" && c.Label == "Gallery section added");
        Assert.Equal(1, added.Data.Counts.Added);

        var removed = await env.Revisions.CompareAsync(env.Owner.Id, rev2.Id, rev1.Id);
        Assert.Contains(removed.Data!.Changes, c => c.Kind == "removed" && c.Label == "Gallery section removed");
        Assert.Equal(1, removed.Data.Counts.Removed);
    }

    [Fact]
    public async Task Compare_ReportsModifiedSectionContentAndBrandColour()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        await Save(env, Config("Same hero"));

        var edited = Config("Same hero");
        edited.Sections.First(s => s.Key == "services").Title = "Renamed services";
        edited.Sections.First(s => s.Key == "about").Enabled = false;
        edited.Theme.Primary = "#123456";
        await Save(env, edited);

        var history = await History(env);
        var compare = await env.Revisions.CompareAsync(
            env.Owner.Id,
            history.Single(r => r.RevisionNumber == 1).Id,
            history.Single(r => r.RevisionNumber == 2).Id);

        Assert.True(compare.Success, compare.Error?.Message);
        var changes = compare.Data!.Changes;
        Assert.Contains(changes, c => c.Label == "Services heading changed");
        Assert.Contains(changes, c => c.Label == "About hidden");
        Assert.Contains(changes, c => c.Category == "Theme" && c.Label == "Primary brand colour changed");

        // A comparison is not a raw payload diff: every change is a labelled
        // sentence and raw property tokens never leak into it.
        Assert.All(changes, c => Assert.False(string.IsNullOrWhiteSpace(c.Label)));
        Assert.All(changes, c => Assert.False(string.IsNullOrWhiteSpace(c.Category)));
        Assert.DoesNotContain(changes, c =>
            c.Label.Contains("MediaId", StringComparison.Ordinal) ||
            c.Label.Contains("MutedText", StringComparison.Ordinal) ||
            c.Label.Contains("ButtonBackground", StringComparison.Ordinal) ||
            c.Label.Contains("ShowInNav", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RevisionHistory_IsTenantIsolated()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        await env.OtherBuilder.SaveDraftAsync(env.OtherOwner.Id, new SaveStorefrontDraftRequest
        {
            Config = Config("Other business hero"),
        });
        var otherRevision = (await env.OtherRevisions.ListAsync(
            env.OtherOwner.Id, new StorefrontRevisionQuery())).Data!.Items.Single();

        await Save(env, Config("My hero"));
        var mine = (await History(env)).First();

        // Reading, comparing and restoring another tenant's revision all look the
        // same as "does not exist" — the API never confirms it exists.
        Assert.Equal("NOT_FOUND", (await env.Revisions.GetAsync(env.Owner.Id, otherRevision.Id)).Error?.Code);
        Assert.Equal("NOT_FOUND", (await env.Revisions.CompareAsync(env.Owner.Id, mine.Id, otherRevision.Id)).Error?.Code);
        Assert.Equal("NOT_FOUND", (await env.Revisions.RestoreAsync(
            env.Owner.Id, otherRevision.Id, new RestoreStorefrontRevisionRequest { Confirm = true })).Error?.Code);

        // ...and the other business cannot see mine.
        Assert.Equal("NOT_FOUND", (await env.OtherRevisions.GetAsync(env.OtherOwner.Id, mine.Id)).Error?.Code);
        var otherList = await env.OtherRevisions.ListAsync(env.OtherOwner.Id, new StorefrontRevisionQuery());
        Assert.DoesNotContain(otherList.Data!.Items, r => r.Id == mine.Id);
    }

    [Fact]
    public async Task Restore_RequiresConfirmationAndOwnership()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        await Save(env, Config("A heading"));
        await Save(env, Config("B heading"));
        var version = (await env.Builder.GetForOwnerAsync(env.Owner.Id)).Data!.DraftVersion;
        var first = (await History(env)).Single(r => r.RevisionNumber == 1);

        // Without an explicit confirmation the restore is refused and nothing moves.
        var unconfirmed = await env.Revisions.RestoreAsync(
            env.Owner.Id, first.Id, new RestoreStorefrontRevisionRequest { ExpectedVersion = version });
        Assert.Equal("VALIDATION_ERROR", unconfirmed.Error?.Code);
        Assert.Equal("B heading", (await DraftAsync(env)).Hero.Heading);
        Assert.Equal(2, (await History(env)).Count);

        // A caller with no owned business cannot restore anything, even holding a
        // valid revision id.
        var stranger = BookingTestBase.CreateOwner("revision-stranger@test.com");
        await BookingTestBase.SeedAsync(context, stranger);
        var strangerService = new StorefrontRevisionService(
            context, Mock.Of<IBusinessScopeResolver>(),
            TestHelpers.CreateLogger<StorefrontRevisionService>(), null);

        var denied = await strangerService.RestoreAsync(
            stranger.Id, first.Id, new RestoreStorefrontRevisionRequest { Confirm = true });
        Assert.Equal("NOT_FOUND", denied.Error?.Code);
        Assert.Equal("B heading", (await DraftAsync(env)).Hero.Heading);
    }

    [Fact]
    public async Task StaleWritesAreRejected_AndDuplicateRevisionNumbersAreImpossible()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        await Save(env, Config("Only heading"));
        var version = (await env.Builder.GetForOwnerAsync(env.Owner.Id)).Data!.DraftVersion;
        var only = (await History(env)).Single();

        // A save based on a draft version that no longer exists is refused rather
        // than silently overwriting the newer work.
        var staleSave = await Save(env, Config("Conflicting heading"), expectedVersion: version + 99);
        Assert.Equal("STALE_VERSION", staleSave.Error?.Code);

        var staleRestore = await env.Revisions.RestoreAsync(env.Owner.Id, only.Id,
            new RestoreStorefrontRevisionRequest { Confirm = true, ExpectedVersion = version + 99 });
        Assert.Equal("STALE_VERSION", staleRestore.Error?.Code);

        // The unique (business, revision number) index is what makes a revision
        // collision impossible at the database level.
        context.StorefrontRevisions.Add(new StorefrontRevision
        {
            Id = Guid.NewGuid(),
            BusinessId = env.Business.Id,
            RevisionNumber = only.RevisionNumber,
            ConfigurationJson = "{}",
            RevisionType = StorefrontRevisionType.DraftSave,
            CreatedAt = DateTime.UtcNow,
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        context.ChangeTracker.Clear();
    }

    [Fact]
    public async Task UnreadableSnapshot_IsRejectedSafely()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        await Save(env, Config("Good heading"));

        var broken = new StorefrontRevision
        {
            Id = Guid.NewGuid(),
            BusinessId = env.Business.Id,
            RevisionNumber = 99,
            ConfigurationJson = "{ this is not valid json",
            RevisionType = StorefrontRevisionType.DraftSave,
            CreatedAt = DateTime.UtcNow,
        };
        context.StorefrontRevisions.Add(broken);
        await context.SaveChangesAsync();

        Assert.Equal("VALIDATION_ERROR", (await env.Revisions.GetAsync(env.Owner.Id, broken.Id)).Error?.Code);
        Assert.Equal("VALIDATION_ERROR", (await env.Revisions.RestoreAsync(
            env.Owner.Id, broken.Id, new RestoreStorefrontRevisionRequest { Confirm = true })).Error?.Code);

        var good = (await History(env)).Single(r => r.RevisionNumber == 1);
        Assert.Equal("VALIDATION_ERROR", (await env.Revisions.CompareAsync(env.Owner.Id, good.Id, broken.Id)).Error?.Code);

        // The draft is untouched by every rejected attempt.
        Assert.Equal("Good heading", (await DraftAsync(env)).Hero.Heading);
        Assert.Equal(1, (await History(env)).Count(r => r.RevisionNumber != 99));
    }

    [Fact]
    public async Task History_PagesAndFilters()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        await Save(env, Config("One"));
        await Save(env, Config("Two"));
        await env.Builder.PublishAsync(env.Owner.Id, new PublishStorefrontRequest());
        await Save(env, Config("Three"));

        var page1 = await List(env, page: 1, pageSize: 2);
        Assert.True(page1.Success, page1.Error?.Message);
        Assert.Equal(2, page1.Data!.Items.Count);
        Assert.Equal(4, page1.Data.TotalCount);
        Assert.Equal(2, page1.Data.TotalPages);
        Assert.True(page1.Data.HasMore);
        Assert.Equal(new[] { 4, 3 }, page1.Data.Items.Select(r => r.RevisionNumber));
        Assert.True(page1.Data.DraftVersion > 0);

        var page2 = await List(env, page: 2, pageSize: 2);
        Assert.False(page2.Data!.HasMore);
        Assert.Equal(new[] { 2, 1 }, page2.Data.Items.Select(r => r.RevisionNumber));

        var publishes = await List(env, type: "publish");
        var publishedRevision = Assert.Single(publishes.Data!.Items);
        Assert.Equal("publish", publishedRevision.RevisionType);

        var snapshots = await List(env, publishedOnly: true);
        Assert.All(snapshots.Data!.Items, r => Assert.True(r.IsPublishedSnapshot));

        Assert.Equal("VALIDATION_ERROR", (await List(env, type: "nonsense")).Error?.Code);

        // A business that has never touched the builder has an empty history.
        var untouched = await env.OtherRevisions.ListAsync(env.OtherOwner.Id, new StorefrontRevisionQuery());
        Assert.Empty(untouched.Data!.Items);
    }

    [Fact]
    public async Task ApplyingATemplate_IsRecordedAsADraftRevision()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        await Save(env, Config("Base heading"));

        var applied = await env.Builder.ApplyTemplateAsync(env.Owner.Id, "luxe");
        Assert.True(applied.Success, applied.Error?.Message);

        var top = (await History(env)).First();
        Assert.Equal("draftSave", top.RevisionType);
        Assert.Equal("Template changed to Luxe", top.Description);

        // Re-applying the same design changes nothing, so it adds no revision.
        await env.Builder.ApplyTemplateAsync(env.Owner.Id, "luxe");
        Assert.Equal(2, (await History(env)).Count);
    }
}
