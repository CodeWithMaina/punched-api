using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Design;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;
using PunchedApi.Infrastructure.Repositories;
using Xunit;

namespace PunchedApi.Tests;

/// <summary>
/// Card-design authoring tests (SQLite in-memory): append-only version history,
/// config validation/asset ownership through the shared pipeline, module-gate
/// read-vs-write semantics, the tenant design cap, preview parity, and
/// cross-tenant isolation on every surface (§5, §15, §16, §20, §36).
/// </summary>
public class CardDesignVersioningTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _db;
    private readonly UnitOfWork _uow;
    private readonly CardDesignService _service;
    private readonly StubModuleEntitlements _entitlements;

    private readonly Guid _ownerId = Guid.NewGuid();
    private readonly Guid _otherOwnerId = Guid.NewGuid();
    private Guid _businessId;
    private Guid _otherBusinessId;

    public CardDesignVersioningTests()
    {
        _connection = BookingTestBase.CreateConnection();
        _db = BookingTestBase.CreateContext(_connection);
        _uow = new UnitOfWork(_db);

        _entitlements = new StubModuleEntitlements(CardDesignResolver.ModuleKey);
        var resolver = new CardDesignResolver(_uow, _entitlements, NullLogger<CardDesignResolver>.Instance);
        _service = new CardDesignService(_uow, resolver, NullLogger<CardDesignService>.Instance);

        Seed();
    }

    public void Dispose()
    {
        _uow.Dispose();
        _db.Dispose();
        _connection.Dispose();
    }

    private void Seed()
    {
        var owner = BookingTestBase.CreateOwner($"own-{Guid.NewGuid():N}@t.com");
        owner.Id = _ownerId;

        var otherOwner = BookingTestBase.CreateOwner($"oth-{Guid.NewGuid():N}@t.com");
        otherOwner.Id = _otherOwnerId;

        var business = BookingTestBase.CreateBusiness(_ownerId, "Design Cafe");
        _businessId = business.Id;

        var otherBusiness = BookingTestBase.CreateBusiness(_otherOwnerId, "Foreign Cafe");
        _otherBusinessId = otherBusiness.Id;

        _db.AddRange(owner, otherOwner, business, otherBusiness);
        _db.SaveChanges();
    }

    // ── Helpers ─────────────────────────────────────────────

    private async Task<CardDesign> CreateDesignAsync(string name = "Branded")
    {
        var created = await _service.CreateBusinessDesignAsync(_ownerId,
            new CreateBusinessCardDesignRequest { Name = name });
        Assert.True(created.Success, created.Error?.Message);
        _db.ChangeTracker.Clear();
        return _db.CardDesigns.AsNoTracking().First(d => d.Id == created.Data!.Id);
    }

    private List<CardDesignVersion> Versions(Guid designId) =>
        _db.CardDesignVersions.AsNoTracking()
            .Where(v => v.CardDesignId == designId)
            .OrderBy(v => v.VersionNumber)
            .ToList();

    private CardDesign Design(Guid id) =>
        _db.CardDesigns.AsNoTracking().First(d => d.Id == id);

    private Guid SeedAsset(Guid businessId, CardAssetStatus status = CardAssetStatus.Active)
    {
        var id = Guid.NewGuid();
        _db.CardAssets.Add(new CardAsset
        {
            Id = id,
            BusinessId = businessId,
            Purpose = CardAssetPurposes.Logo,
            Kind = CardAssetKind.Image,
            ContentType = "image/png",
            FileExtension = "png",
            SizeBytes = 64,
            Width = 8,
            Height = 8,
            StorageKey = $"{businessId}/{id}.png",
            Sha256 = new string('a', 64),
            Status = status,
            UpdatedAt = DateTime.UtcNow,
            // ck_card_assets_deleted_at_consistent: Deleted ⇔ DeletedAt set.
            DeletedAt = status == CardAssetStatus.Deleted ? DateTime.UtcNow : null,
            CreatedAt = DateTime.UtcNow
        });
        _db.SaveChanges();
        return id;
    }

    // ── Version history (append-only) ──────────────────────

    [Fact]
    public async Task Create_RecordsVersionOne_WithConfigSnapshot()
    {
        var design = await CreateDesignAsync("Branded");

        Assert.Equal(1, design.CurrentVersion);
        Assert.False(string.IsNullOrWhiteSpace(design.ConfigJson));
        Assert.False(string.IsNullOrWhiteSpace(design.HtmlTemplate));

        var version = Assert.Single(Versions(design.Id));
        Assert.Equal(1, version.VersionNumber);
        Assert.Equal("Design created", version.ChangeNote);
        Assert.Equal(_ownerId, version.PublishedByUserId);
        Assert.Equal(design.ConfigJson, version.ConfigJson);
        Assert.Equal(design.HtmlTemplate, version.HtmlTemplate);
    }

    [Fact]
    public async Task AdminCreate_AlsoRecordsVersionOne()
    {
        var created = await _service.CreateDesignAsync(_businessId,
            new CreateCardDesignRequest { Name = "Admin authored", HtmlTemplate = "<div>hello</div>" },
            Guid.NewGuid());
        Assert.True(created.Success, created.Error?.Message);
        _db.ChangeTracker.Clear();

        var version = Assert.Single(Versions(created.Data!.Id));
        Assert.Equal(1, version.VersionNumber);
        Assert.Equal(1, Design(created.Data.Id).CurrentVersion);
    }

    [Fact]
    public async Task Update_ContentChange_AppendsVersion_WithoutRewritingHistory()
    {
        var design = await CreateDesignAsync("Original");

        var update = await _service.UpdateBusinessDesignAsync(_ownerId, design.Id,
            new UpdateBusinessCardDesignRequest { Name = "Renamed", ChangeNote = "Rebrand" });
        Assert.True(update.Success, update.Error?.Message);
        Assert.Equal(2, update.Data!.CurrentVersion);
        _db.ChangeTracker.Clear();

        var versions = Versions(design.Id);
        Assert.Equal(2, versions.Count);

        // Append-only: the historical row still carries the original content.
        Assert.Equal("Original", versions[0].Name);
        Assert.Equal("Design created", versions[0].ChangeNote);
        Assert.Equal("Renamed", versions[1].Name);
        Assert.Equal("Rebrand", versions[1].ChangeNote);
        Assert.Equal("Renamed", Design(design.Id).Name);
    }

    [Fact]
    public async Task Update_NoOpRequest_DoesNotSpamVersions()
    {
        var design = await CreateDesignAsync("Stable");

        var update = await _service.UpdateBusinessDesignAsync(_ownerId, design.Id,
            new UpdateBusinessCardDesignRequest { Name = "Stable" });
        Assert.True(update.Success, update.Error?.Message);
        Assert.Equal(1, update.Data!.CurrentVersion);
        _db.ChangeTracker.Clear();

        Assert.Single(Versions(design.Id));
    }

    [Fact]
    public async Task ConcurrentVersionAppend_IsRejectedAsConflict()
    {
        var design = await CreateDesignAsync("Racy");

        // Simulate a racing writer that already published version 2 — the unique
        // (design, version) index makes the loser lose deterministically instead
        // of forking the history.
        _db.CardDesignVersions.Add(new CardDesignVersion
        {
            Id = Guid.NewGuid(),
            CardDesignId = design.Id,
            VersionNumber = 2,
            Name = "Raced",
            HtmlTemplate = "<div>raced</div>",
            PublishedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var update = await _service.UpdateBusinessDesignAsync(_ownerId, design.Id,
            new UpdateBusinessCardDesignRequest { Name = "Loser" });

        Assert.False(update.Success);
        Assert.Equal("CONFLICT", update.Error!.Code);

        // The losing transaction rolled back: name unchanged, version not bumped.
        Assert.Equal("Racy", Design(design.Id).Name);
        Assert.Equal(1, Design(design.Id).CurrentVersion);
    }

    [Fact]
    public async Task Versions_AreListedNewestFirst_WithCurrentFlag()
    {
        var design = await CreateDesignAsync("History");
        await _service.UpdateBusinessDesignAsync(_ownerId, design.Id,
            new UpdateBusinessCardDesignRequest { Name = "History v2" });
        _db.ChangeTracker.Clear();

        var result = await _service.GetVersionsForBusinessAsync(_ownerId, design.Id);

        Assert.True(result.Success, result.Error?.Message);
        var versions = result.Data!;
        Assert.Equal(2, versions.Count);
        Assert.Equal(2, versions[0].VersionNumber);
        Assert.True(versions[0].IsCurrent);
        Assert.False(versions[1].IsCurrent);
    }

    [Fact]
    public async Task Versions_CrossTenant_ReportsNotFound()
    {
        var design = await CreateDesignAsync("Private");

        var result = await _service.GetVersionsForBusinessAsync(_otherOwnerId, design.Id);

        Assert.False(result.Success);
        Assert.Equal("NOT_FOUND", result.Error!.Code);
    }

    [Fact]
    public async Task Update_CrossTenant_ReportsNotFoundAndLeavesDesignUntouched()
    {
        var design = await CreateDesignAsync("Private");

        var update = await _service.UpdateBusinessDesignAsync(_otherOwnerId, design.Id,
            new UpdateBusinessCardDesignRequest { Name = "Hijacked" });

        Assert.False(update.Success);
        Assert.Equal("NOT_FOUND", update.Error!.Code);

        _db.ChangeTracker.Clear();
        Assert.Equal("Private", Design(design.Id).Name);
        Assert.Single(Versions(design.Id));
    }

    [Fact]
    public async Task GetMyDesigns_NeverLeaksOtherTenantsOrThePlatformDefault()
    {
        await CreateDesignAsync("Mine");
        await _service.CreateDesignAsync(_otherBusinessId,
            new CreateCardDesignRequest { Name = "Theirs", HtmlTemplate = "<div>other</div>" }, null);

        // Platform default design (system-wide) must never appear as "mine".
        _db.CardDesigns.Add(new CardDesign
        {
            Id = Guid.NewGuid(),
            BusinessId = null,
            Name = "Platform Default",
            HtmlTemplate = "<div>default</div>",
            IsActive = true,
            IsDefault = true,
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var result = await _service.GetMyDesignsAsync(_ownerId);

        Assert.True(result.Success, result.Error?.Message);
        var design = Assert.Single(result.Data!);
        Assert.Equal("Mine", design.Name);
    }

    // ── Module gate: reads allowed, writes fail-closed (§12) ──

    [Fact]
    public async Task ModuleOff_ReadsAllowed_WritesFailClosed()
    {
        var design = await CreateDesignAsync("Kept");
        _entitlements.Disable(CardDesignResolver.ModuleKey);

        // A downgrade must never hide or delete what already exists…
        var list = await _service.GetMyDesignsAsync(_ownerId);
        Assert.True(list.Success, list.Error?.Message);
        Assert.Single(list.Data!);

        var versions = await _service.GetVersionsForBusinessAsync(_ownerId, design.Id);
        Assert.True(versions.Success, versions.Error?.Message);

        var savedPreview = await _service.PreviewForBusinessAsync(_ownerId,
            new PreviewCardDesignRequest { CardDesignId = design.Id });
        Assert.True(savedPreview.Success, savedPreview.Error?.Message);

        // …but new authoring is refused, fail-closed.
        var create = await _service.CreateBusinessDesignAsync(_ownerId,
            new CreateBusinessCardDesignRequest { Name = "New" });
        Assert.False(create.Success);
        Assert.Equal("MODULE_DISABLED", create.Error!.Code);

        var update = await _service.UpdateBusinessDesignAsync(_ownerId, design.Id,
            new UpdateBusinessCardDesignRequest { Name = "Renamed" });
        Assert.False(update.Success);
        Assert.Equal("MODULE_DISABLED", update.Error!.Code);

        var configPreview = await _service.PreviewForBusinessAsync(_ownerId,
            new PreviewCardDesignRequest { Config = CardDesignConfig.CreateDefault() });
        Assert.False(configPreview.Success);
        Assert.Equal("MODULE_DISABLED", configPreview.Error!.Code);

        // Nothing slipped through.
        _db.ChangeTracker.Clear();
        Assert.Equal("Kept", Design(design.Id).Name);
        Assert.Equal(0, _db.CardDesigns.AsNoTracking().Count(d => d.BusinessId == _businessId && d.Name == "New"));
    }

    // ── Tenant cap + name validation ───────────────────────

    [Fact]
    public async Task Create_EnforcesTenantDesignCap()
    {
        for (var i = 0; i < 25; i++)
        {
            _db.CardDesigns.Add(new CardDesign
            {
                Id = Guid.NewGuid(),
                BusinessId = _businessId,
                Name = $"Bulk {i}",
                HtmlTemplate = "<div>x</div>",
                IsActive = true,
                IsDefault = false,
                CreatedAt = DateTime.UtcNow
            });
        }
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var create = await _service.CreateBusinessDesignAsync(_ownerId,
            new CreateBusinessCardDesignRequest { Name = "One too many" });

        Assert.False(create.Success);
        Assert.Equal("DESIGN_LIMIT_REACHED", create.Error!.Code);
        Assert.Equal(25, _db.CardDesigns.AsNoTracking()
            .Count(d => d.BusinessId == _businessId && !d.IsDefault));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_RejectsEmptyNames(string name)
    {
        var create = await _service.CreateBusinessDesignAsync(_ownerId,
            new CreateBusinessCardDesignRequest { Name = name });

        Assert.False(create.Success);
        Assert.Equal("INVALID_NAME", create.Error!.Code);
    }

    [Fact]
    public async Task Create_RejectsOverlongNames()
    {
        var create = await _service.CreateBusinessDesignAsync(_ownerId,
            new CreateBusinessCardDesignRequest { Name = new string('x', 101) });

        Assert.False(create.Success);
        Assert.Equal("INVALID_NAME", create.Error!.Code);
    }

    // ── Config validation + asset ownership (one evaluator) ──

    [Fact]
    public async Task Create_InvalidConfig_IsRejectedWithoutPersisting()
    {
        var config = CardDesignConfig.CreateDefault();
        config.Layout.StampGrid.Columns = 999; // above CardDesignConfigValidator.MaxStampColumns

        var create = await _service.CreateBusinessDesignAsync(_ownerId,
            new CreateBusinessCardDesignRequest { Name = "Broken", Config = config });

        Assert.False(create.Success);
        Assert.Equal("INVALID_CONFIG", create.Error!.Code);
        Assert.Equal(0, _db.CardDesigns.AsNoTracking()
            .Count(d => d.BusinessId == _businessId && d.Name == "Broken"));
    }

    [Fact]
    public async Task Create_WithOwnActiveAsset_Succeeds()
    {
        var assetId = SeedAsset(_businessId);
        var config = CardDesignConfig.CreateDefault();
        config.Logo.AssetId = assetId;

        var create = await _service.CreateBusinessDesignAsync(_ownerId,
            new CreateBusinessCardDesignRequest { Name = "With logo", Config = config });

        Assert.True(create.Success, create.Error?.Message);
        Assert.Contains(assetId.ToString(), Design(create.Data!.Id).HtmlTemplate);
    }

    [Fact]
    public async Task Create_WithForeignTenantAsset_IsRejected()
    {
        // Another business's asset: same error as "does not exist" so the
        // response never confirms that the foreign id is real (§20).
        var foreignAssetId = SeedAsset(_otherBusinessId);
        var config = CardDesignConfig.CreateDefault();
        config.Logo.AssetId = foreignAssetId;

        var create = await _service.CreateBusinessDesignAsync(_ownerId,
            new CreateBusinessCardDesignRequest { Name = "Sneaky", Config = config });

        Assert.False(create.Success);
        Assert.Equal("ASSET_NOT_AVAILABLE", create.Error!.Code);
        Assert.Equal(0, _db.CardDesigns.AsNoTracking()
            .Count(d => d.BusinessId == _businessId && d.Name == "Sneaky"));
    }

    [Fact]
    public async Task Create_WithDeletedAsset_IsRejected()
    {
        var assetId = SeedAsset(_businessId, CardAssetStatus.Deleted);
        var config = CardDesignConfig.CreateDefault();
        config.Logo.AssetId = assetId;

        var create = await _service.CreateBusinessDesignAsync(_ownerId,
            new CreateBusinessCardDesignRequest { Name = "Stale ref", Config = config });

        Assert.False(create.Success);
        Assert.Equal("ASSET_NOT_AVAILABLE", create.Error!.Code);
    }

    // ── Preview parity + tenant isolation (§15, §16) ───────

    [Fact]
    public async Task Preview_Config_RendersThroughTheSharedPipeline()
    {
        var preview = await _service.PreviewForBusinessAsync(_ownerId,
            new PreviewCardDesignRequest { Config = CardDesignConfig.CreateDefault() });

        Assert.True(preview.Success, preview.Error?.Message);
        Assert.Contains("punched-card", preview.Data!.SanitizedTemplate);
        Assert.Contains("Design Cafe", preview.Data.RenderedHtml);
        Assert.NotEmpty(preview.Data.Variables);
    }

    [Fact]
    public async Task Preview_RawHtml_IsSanitizedBeforeRendering()
    {
        var preview = await _service.PreviewForBusinessAsync(_ownerId,
            new PreviewCardDesignRequest
            {
                HtmlTemplate = "<div onclick=\"steal()\"><script>alert(1)</script>safe</div>"
            });

        Assert.True(preview.Success, preview.Error?.Message);
        Assert.DoesNotContain("script", preview.Data!.SanitizedTemplate, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", preview.Data.SanitizedTemplate, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", preview.Data.RenderedHtml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Preview_ForeignSavedDesign_IsNotFound()
    {
        var foreign = await _service.CreateDesignAsync(_otherBusinessId,
            new CreateCardDesignRequest { Name = "Theirs", HtmlTemplate = "<div>secret</div>" }, null);
        Assert.True(foreign.Success, foreign.Error?.Message);

        var preview = await _service.PreviewForBusinessAsync(_ownerId,
            new PreviewCardDesignRequest { CardDesignId = foreign.Data!.Id });

        Assert.False(preview.Success);
        Assert.Equal("NOT_FOUND", preview.Error!.Code);
    }

    [Fact]
    public async Task Preview_ForeignAssetInConfig_IsRejected()
    {
        var foreignAssetId = SeedAsset(_otherBusinessId);
        var config = CardDesignConfig.CreateDefault();
        config.Logo.AssetId = foreignAssetId;

        var preview = await _service.PreviewForBusinessAsync(_ownerId,
            new PreviewCardDesignRequest { Config = config });

        Assert.False(preview.Success);
        Assert.Equal("ASSET_NOT_AVAILABLE", preview.Error!.Code);
    }

    [Fact]
    public async Task Preview_UnknownOwner_IsNotFound()
    {
        var preview = await _service.PreviewForBusinessAsync(Guid.NewGuid(),
            new PreviewCardDesignRequest { Config = CardDesignConfig.CreateDefault() });

        Assert.False(preview.Success);
        Assert.Equal("NOT_FOUND", preview.Error!.Code);
    }
}



