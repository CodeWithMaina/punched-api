using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;
using PunchedApi.Infrastructure.Repositories;
using Xunit;

namespace PunchedApi.Tests;

/// <summary>
/// Governance tests for stamp-card *business rules* (SQLite in-memory):
/// a rules edit is audited, never silently rewrites existing enrolments unless
/// explicitly opted in with a reason, and never gets confused with presentation
/// (design) changes. Also covers lifecycle transitions, strict status parsing,
/// required-stamp boundaries and cross-tenant isolation (§16, §20, §36).
/// </summary>
public class StampCardRulesChangeTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _db;
    private readonly UnitOfWork _uow;
    private readonly StampCardService _service;
    private readonly CardDesignService _designService;
    private readonly StubModuleEntitlements _entitlements;

    private readonly Guid _ownerId = Guid.NewGuid();
    private readonly Guid _otherOwnerId = Guid.NewGuid();
    private Guid _businessId;
    private Guid _programId;

    public StampCardRulesChangeTests()
    {
        _connection = BookingTestBase.CreateConnection();
        _db = BookingTestBase.CreateContext(_connection);
        _uow = new UnitOfWork(_db);

        _entitlements = new StubModuleEntitlements(CardDesignResolver.ModuleKey);
        var resolver = new CardDesignResolver(_uow, _entitlements, NullLogger<CardDesignResolver>.Instance);
        _designService = new CardDesignService(_uow, resolver, NullLogger<CardDesignService>.Instance);
        _service = new StampCardService(_uow, _designService, NullLogger<StampCardService>.Instance);

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

        var business = BookingTestBase.CreateBusiness(_ownerId, "Rules Cafe");
        _businessId = business.Id;

        var otherBusiness = BookingTestBase.CreateBusiness(_otherOwnerId, "Foreign Cafe");

        var program = new LoyaltyProgram
        {
            Id = Guid.NewGuid(),
            BusinessId = _businessId,
            Name = "Coffee Rewards",
            IsActive = true,
            StampsRequired = 10,
            RewardValue = 50,
            RewardDescription = "Free Coffee",
            Status = ProgramStatus.Active,
            CreatedAt = DateTime.UtcNow
        };
        _programId = program.Id;

        _db.AddRange(owner, otherOwner, business, otherBusiness, program);
        _db.SaveChanges();
    }

    // ── Helpers ─────────────────────────────────────────────

    private async Task<Guid> CreateCardAsync(int stampsRequired = 10)
    {
        var created = await _service.CreateStampCardAsync(_ownerId, _programId, new CreateStampCardRequest
        {
            Name = "Main Card",
            StampsRequired = stampsRequired,
            RewardDescription = "Free Coffee",
            RewardValue = 50
        });

        Assert.True(created.Success, created.Error?.Message);
        return created.Data!.Id;
    }

    private async Task<Guid> CreateActiveCardAsync(int stampsRequired = 10)
    {
        var cardId = await CreateCardAsync(stampsRequired);
        var status = await _service.UpdateStampCardStatusAsync(
            _ownerId, cardId, new UpdateStampCardStatusRequest { Status = "active" });
        Assert.True(status.Success, status.Error?.Message);
        return cardId;
    }

    /// <summary>Enrolls a customer whose snapshot is frozen against <paramref name="cardId"/>.</summary>
    private async Task<Guid> EnrollBoundCustomerAsync(Guid cardId, int requiredStamps, int rulesVersion)
    {
        var customer = BookingTestBase.CreateCustomer($"cust-{Guid.NewGuid():N}@t.com");
        var loyaltyCard = new LoyaltyCard
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            BusinessId = _businessId,
            ProgramId = _programId,
            TotalStamps = 4,
            LifetimeStamps = 4,
            StampCardId = cardId,
            RequiredStamps = requiredStamps,
            RulesVersion = rulesVersion,
            EnrolledAt = DateTime.UtcNow.AddDays(-14),
            CreatedAt = DateTime.UtcNow.AddDays(-14)
        };

        _db.AddRange(customer, loyaltyCard);
        await _db.SaveChangesAsync();
        return loyaltyCard.Id;
    }

    /// <summary>Enrolls a customer NOT bound to any stamp card (program-level defaults).</summary>
    private async Task<Guid> EnrollUnboundCustomerAsync()
    {
        var customer = BookingTestBase.CreateCustomer($"cust-{Guid.NewGuid():N}@t.com");
        var loyaltyCard = new LoyaltyCard
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            BusinessId = _businessId,
            ProgramId = _programId,
            TotalStamps = 2,
            LifetimeStamps = 2,
            StampCardId = null,
            RequiredStamps = 10,
            RulesVersion = 0,
            EnrolledAt = DateTime.UtcNow.AddDays(-7),
            CreatedAt = DateTime.UtcNow.AddDays(-7)
        };

        _db.AddRange(customer, loyaltyCard);
        await _db.SaveChangesAsync();
        return loyaltyCard.Id;
    }

    /// <summary>Detaches tracked entities so assertions always read persisted state.</summary>
    private void Reload() => _db.ChangeTracker.Clear();

    private LoyaltyCard LoyaltyCard(Guid id) =>
        _db.LoyaltyCards.AsNoTracking().First(c => c.Id == id);

    private StampCard StampCard(Guid id) =>
        _db.StampCards.AsNoTracking().First(c => c.Id == id);

    private List<StampCardRulesChange> AuditRows(Guid cardId) =>
        _db.StampCardRulesChanges.AsNoTracking()
            .Where(r => r.StampCardId == cardId)
            .OrderBy(r => r.CreatedAt)
            .ToList();

    // ── Rules change: no silent data mutation (§36) ────────

    [Fact]
    public async Task RulesChange_WithoutOptIn_NeverMutatesExistingEnrolments()
    {
        var cardId = await CreateActiveCardAsync(stampsRequired: 10);
        var loyaltyId = await EnrollBoundCustomerAsync(cardId, requiredStamps: 10, rulesVersion: 1);

        var update = await _service.UpdateStampCardAsync(_ownerId, cardId, new UpdateStampCardRequest
        {
            StampsRequired = 20
        });
        Assert.True(update.Success, update.Error?.Message);
        Assert.Equal(2, update.Data!.RulesVersion);

        Reload();

        // The card's own rule changed…
        var card = StampCard(cardId);
        Assert.Equal(20, card.StampsRequired);
        Assert.Equal(2, card.RulesVersion);

        // …but the customer keeps the rule they enrolled under.
        var loyalty = LoyaltyCard(loyaltyId);
        Assert.Equal(10, loyalty.RequiredStamps);
        Assert.Equal(1, loyalty.RulesVersion);
        Assert.Equal(cardId, loyalty.StampCardId);

        // And the change is fully audited.
        var audit = Assert.Single(AuditRows(cardId));
        Assert.Equal("StampsRequired", audit.Field); // nameof would bind the local helper, not the entity
        Assert.Equal("10", audit.OldValue);
        Assert.Equal("20", audit.NewValue);
        Assert.False(audit.AppliedToExistingCards);
        Assert.Equal(0, audit.AffectedCards);
        Assert.Null(audit.Reason);
        Assert.Equal(2, audit.RulesVersion);
        Assert.Equal(_businessId, audit.BusinessId);
        Assert.Equal(_ownerId, audit.ChangedByUserId);
        Assert.Equal("Business", audit.ChangedByRole);
    }

    [Fact]
    public async Task RulesChange_WithOptInAndReason_ResnapshodsAndCountsAffectedCards()
    {
        var cardId = await CreateActiveCardAsync(stampsRequired: 10);
        var loyaltyId = await EnrollBoundCustomerAsync(cardId, requiredStamps: 10, rulesVersion: 1);

        var update = await _service.UpdateStampCardAsync(_ownerId, cardId, new UpdateStampCardRequest
        {
            StampsRequired = 20,
            ApplyToExistingCards = true,
            Reason = "Promotion extends the goal to 20 stamps"
        });
        Assert.True(update.Success, update.Error?.Message);

        Reload();

        var loyalty = LoyaltyCard(loyaltyId);
        Assert.Equal(20, loyalty.RequiredStamps);
        Assert.Equal(2, loyalty.RulesVersion);

        var audit = Assert.Single(AuditRows(cardId));
        Assert.True(audit.AppliedToExistingCards);
        Assert.Equal(1, audit.AffectedCards);
        Assert.Equal("Promotion extends the goal to 20 stamps", audit.Reason);
    }

    [Fact]
    public async Task OptInWithoutReason_IsRejectedAndNothingIsPersisted()
    {
        var cardId = await CreateActiveCardAsync(stampsRequired: 10);
        var loyaltyId = await EnrollBoundCustomerAsync(cardId, requiredStamps: 10, rulesVersion: 1);

        var update = await _service.UpdateStampCardAsync(_ownerId, cardId, new UpdateStampCardRequest
        {
            StampsRequired = 20,
            ApplyToExistingCards = true // no reason → must never silently downgrade to "ignored"
        });

        Assert.False(update.Success);
        Assert.Equal("REASON_REQUIRED", update.Error!.Code);

        Reload();

        Assert.Equal(10, StampCard(cardId).StampsRequired);
        Assert.Equal(1, StampCard(cardId).RulesVersion);
        Assert.Equal(10, LoyaltyCard(loyaltyId).RequiredStamps);
        Assert.Empty(AuditRows(cardId));
    }

    [Fact]
    public async Task OptIn_OnlyRebindsCardsBoundToThisCard()
    {
        var cardId = await CreateActiveCardAsync(stampsRequired: 10);
        var boundId = await EnrollBoundCustomerAsync(cardId, requiredStamps: 10, rulesVersion: 1);
        var unboundId = await EnrollUnboundCustomerAsync();

        var update = await _service.UpdateStampCardAsync(_ownerId, cardId, new UpdateStampCardRequest
        {
            StampsRequired = 20,
            ApplyToExistingCards = true,
            Reason = "Audited bulk change"
        });
        Assert.True(update.Success, update.Error?.Message);

        Reload();

        // Only bound enrolments are counted and re-snapshotted.
        var audit = Assert.Single(AuditRows(cardId));
        Assert.Equal(1, audit.AffectedCards);

        Assert.Equal(20, LoyaltyCard(boundId).RequiredStamps);

        var unbound = LoyaltyCard(unboundId);
        Assert.Null(unbound.StampCardId);
        Assert.Equal(10, unbound.RequiredStamps);
        Assert.Equal(0, unbound.RulesVersion);
    }

    // ── Rules-neutral edits never bump the version ─────────

    [Fact]
    public async Task RulesNeutralUpdate_WritesNoAuditRowAndKeepsVersion()
    {
        var cardId = await CreateActiveCardAsync(stampsRequired: 10);
        var loyaltyId = await EnrollBoundCustomerAsync(cardId, requiredStamps: 10, rulesVersion: 1);

        var update = await _service.UpdateStampCardAsync(_ownerId, cardId, new UpdateStampCardRequest
        {
            Name = "Renamed Card",
            Description = "Cosmetic only"
        });
        Assert.True(update.Success, update.Error?.Message);
        Assert.Equal(1, update.Data!.RulesVersion);

        Reload();

        Assert.Equal(1, StampCard(cardId).RulesVersion);
        Assert.Empty(AuditRows(cardId));
        Assert.Equal(10, LoyaltyCard(loyaltyId).RequiredStamps);
    }

    [Fact]
    public async Task DesignAssignment_WritesNoRulesAuditAndLeavesSnapshotUntouched()
    {
        var cardId = await CreateActiveCardAsync(stampsRequired: 10);
        var loyaltyId = await EnrollBoundCustomerAsync(cardId, requiredStamps: 10, rulesVersion: 1);

        var design = await _designService.CreateDesignAsync(_businessId,
            new CreateCardDesignRequest { Name = "Christmas", HtmlTemplate = "<div>festive</div>" }, null);
        Assert.True(design.Success, design.Error?.Message);

        var update = await _service.UpdateStampCardAsync(_ownerId, cardId, new UpdateStampCardRequest
        {
            CardDesignId = design.Data!.Id
        });
        Assert.True(update.Success, update.Error?.Message);

        Reload();

        // Presentation changed; loyalty state and version did not.
        Assert.Equal(design.Data.Id, StampCard(cardId).CardDesignId);
        Assert.Equal(1, StampCard(cardId).RulesVersion);
        Assert.Empty(AuditRows(cardId));

        var loyalty = LoyaltyCard(loyaltyId);
        Assert.Equal(10, loyalty.RequiredStamps);
        Assert.Equal(1, loyalty.RulesVersion);
    }

    // ── Validation boundaries (server-side, not just DTO) ──

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    [InlineData(-1)]
    public async Task Update_RejectsOutOfRangeStampsRequired(int value)
    {
        var cardId = await CreateActiveCardAsync(stampsRequired: 10);

        var update = await _service.UpdateStampCardAsync(_ownerId, cardId, new UpdateStampCardRequest
        {
            StampsRequired = value
        });

        Assert.False(update.Success);
        Assert.Equal("INVALID_STAMPS_REQUIRED", update.Error!.Code);

        Reload();
        Assert.Equal(10, StampCard(cardId).StampsRequired);
        Assert.Empty(AuditRows(cardId));
    }

    [Theory]
    [InlineData(1, true)]    // min
    [InlineData(100, true)]  // max
    [InlineData(0, false)]
    [InlineData(101, false)]
    public async Task Create_EnforcesStampsRequiredBoundaries(int value, bool shouldSucceed)
    {
        var created = await _service.CreateStampCardAsync(_ownerId, _programId, new CreateStampCardRequest
        {
            Name = "Boundary",
            StampsRequired = value,
            RewardDescription = "Reward",
            RewardValue = 10
        });

        Assert.Equal(shouldSucceed, created.Success);
        if (!shouldSucceed)
            Assert.Equal("INVALID_STAMPS_REQUIRED", created.Error!.Code);
    }

    // ── Lifecycle + strict status parsing ──────────────────

    [Fact]
    public async Task Lifecycle_ValidTransitions_Apply()
    {
        var cardId = await CreateCardAsync(); // draft

        var toActive = await _service.UpdateStampCardStatusAsync(
            _ownerId, cardId, new UpdateStampCardStatusRequest { Status = "active" });
        Assert.True(toActive.Success, toActive.Error?.Message);
        Assert.Equal("active", toActive.Data!.Status);

        var toInactive = await _service.UpdateStampCardStatusAsync(
            _ownerId, cardId, new UpdateStampCardStatusRequest { Status = "inactive" });
        Assert.True(toInactive.Success, toInactive.Error?.Message);

        var backToActive = await _service.UpdateStampCardStatusAsync(
            _ownerId, cardId, new UpdateStampCardStatusRequest { Status = "active" });
        Assert.True(backToActive.Success, backToActive.Error?.Message);

        var toArchived = await _service.UpdateStampCardStatusAsync(
            _ownerId, cardId, new UpdateStampCardStatusRequest { Status = "archived" });
        Assert.True(toArchived.Success, toArchived.Error?.Message);
    }

    [Fact]
    public async Task Lifecycle_RejectsUnlaunchAndTerminalViolations()
    {
        var cardId = await CreateActiveCardAsync();

        var backToDraft = await _service.UpdateStampCardStatusAsync(
            _ownerId, cardId, new UpdateStampCardStatusRequest { Status = "draft" });
        Assert.False(backToDraft.Success);
        Assert.Equal("INVALID_TRANSITION", backToDraft.Error!.Code);

        var archived = await _service.UpdateStampCardStatusAsync(
            _ownerId, cardId, new UpdateStampCardStatusRequest { Status = "archived" });
        Assert.True(archived.Success, archived.Error?.Message);

        var revive = await _service.UpdateStampCardStatusAsync(
            _ownerId, cardId, new UpdateStampCardStatusRequest { Status = "active" });
        Assert.False(revive.Success);
        Assert.Equal("INVALID_TRANSITION", revive.Error!.Code);
    }

    [Theory]
    [InlineData("paused")]
    [InlineData("")]
    [InlineData("ACTIVATE")] // unknown values are never coerced — even upper-case typos
    public async Task Lifecycle_UnknownStatus_IsRejected(string status)
    {
        var cardId = await CreateActiveCardAsync();

        var result = await _service.UpdateStampCardStatusAsync(
            _ownerId, cardId, new UpdateStampCardStatusRequest { Status = status });

        Assert.False(result.Success);
        Assert.Equal("INVALID_STATUS", result.Error!.Code);
        Assert.Equal(StampCardStatus.Active, StampCard(cardId).Status);
    }

    [Fact]
    public async Task ArchivedCard_RejectsEdits()
    {
        var cardId = await CreateActiveCardAsync(stampsRequired: 10);
        await _service.UpdateStampCardStatusAsync(
            _ownerId, cardId, new UpdateStampCardStatusRequest { Status = "archived" });

        var update = await _service.UpdateStampCardAsync(_ownerId, cardId, new UpdateStampCardRequest
        {
            StampsRequired = 20
        });

        Assert.False(update.Success);
        Assert.Equal("ARCHIVED", update.Error!.Code);

        Reload();
        Assert.Equal(10, StampCard(cardId).StampsRequired);
        Assert.Empty(AuditRows(cardId));
    }

    [Fact]
    public async Task Delete_OnlyAllowedForNonLiveCards()
    {
        var cardId = await CreateActiveCardAsync();

        var deleteActive = await _service.DeleteStampCardAsync(_ownerId, cardId);
        Assert.False(deleteActive.Success);
        Assert.Equal("NOT_SAFE", deleteActive.Error!.Code);

        await _service.UpdateStampCardStatusAsync(
            _ownerId, cardId, new UpdateStampCardStatusRequest { Status = "archived" });

        var deleteArchived = await _service.DeleteStampCardAsync(_ownerId, cardId);
        Assert.True(deleteArchived.Success, deleteArchived.Error?.Message);
    }

    // ── Cross-tenant isolation (§20) ───────────────────────

    [Fact]
    public async Task ForeignOwner_NeverSeesOrMutatesAnotherTenantsCard()
    {
        var cardId = await CreateActiveCardAsync(stampsRequired: 10);

        // Reads report NOT_FOUND — never a distinct "forbidden" that would
        // confirm the id exists in another tenant.
        var read = await _service.GetStampCardAsync(_otherOwnerId, cardId);
        Assert.False(read.Success);
        Assert.Equal("NOT_FOUND", read.Error!.Code);

        // Writes are equally invisible and leave no trace.
        var update = await _service.UpdateStampCardAsync(_otherOwnerId, cardId, new UpdateStampCardRequest
        {
            StampsRequired = 99,
            ApplyToExistingCards = true,
            Reason = "hostile cross-tenant edit"
        });
        Assert.False(update.Success);
        Assert.Equal("NOT_FOUND", update.Error!.Code);

        var status = await _service.UpdateStampCardStatusAsync(
            _otherOwnerId, cardId, new UpdateStampCardStatusRequest { Status = "archived" });
        Assert.False(status.Success);
        Assert.Equal("NOT_FOUND", status.Error!.Code);

        Reload();
        var card = StampCard(cardId);
        Assert.Equal(10, card.StampsRequired);
        Assert.Equal(StampCardStatus.Active, card.Status);
        Assert.Equal(1, card.RulesVersion);
        Assert.Empty(AuditRows(cardId));
    }

    [Fact]
    public async Task ForeignOwner_CannotDeleteAnotherTenantsCard()
    {
        var cardId = await CreateActiveCardAsync();

        var delete = await _service.DeleteStampCardAsync(_otherOwnerId, cardId);

        Assert.False(delete.Success);
        Assert.Equal("NOT_FOUND", delete.Error!.Code);
        Assert.True(_db.StampCards.AsNoTracking().Any(c => c.Id == cardId));
    }
}



