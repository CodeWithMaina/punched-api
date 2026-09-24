using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.Loyalty;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;
using PunchedApi.Infrastructure.Repositories;
using Xunit;

namespace PunchedApi.Tests;

/// <summary>
/// Tests for the enrolment rules snapshot (§36, no silent data mutation):
/// <see cref="LoyaltyCardSnapshot.Apply"/> freezes the rule a customer joined
/// under, and <see cref="LoyaltyCardSnapshot.ResolveDefaultStampCardAsync"/>
/// only binds when the program has exactly ONE active stamp card — 0 or many
/// active cards are ambiguous and must fall back to program-level defaults.
/// </summary>
public class LoyaltyCardSnapshotTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _db;
    private readonly UnitOfWork _uow;

    public LoyaltyCardSnapshotTests()
    {
        _connection = BookingTestBase.CreateConnection();
        _db = BookingTestBase.CreateContext(_connection);
        _uow = new UnitOfWork(_db);
    }

    public void Dispose()
    {
        _uow.Dispose();
        _db.Dispose();
        _connection.Dispose();
    }

    // ── Builders ────────────────────────────────────────────

    private static StampCard Card(
        Guid programId, StampCardStatus status, int stampsRequired = 10,
        DateTime? createdAt = null) => new()
    {
        Id = Guid.NewGuid(),
        ProgramId = programId,
        BusinessId = Guid.NewGuid(),
        Name = "Card",
        StampsRequired = stampsRequired,
        RewardDescription = "Card reward",
        RewardValue = 50,
        Status = status,
        RulesVersion = 3,
        CreatedAt = createdAt ?? DateTime.UtcNow
    };

    private static LoyaltyProgram Program(int stampsRequired = 10) => new()
    {
        Id = Guid.NewGuid(),
        BusinessId = Guid.NewGuid(),
        Name = "Rewards",
        StampsRequired = stampsRequired,
        RewardDescription = "Program reward",
        CreatedAt = DateTime.UtcNow
    };

    private static LoyaltyCard Loyalty(int requiredStamps, Guid? stampCardId = null) => new()
    {
        Id = Guid.NewGuid(),
        CustomerId = Guid.NewGuid(),
        BusinessId = Guid.NewGuid(),
        ProgramId = Guid.NewGuid(),
        RequiredStamps = requiredStamps,
        StampCardId = stampCardId,
        EnrolledAt = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow
    };

    // ── Apply (freeze on enrollment) ───────────────────────

    [Fact]
    public void Apply_SingleActiveCard_BindsAndFreezesSnapshot()
    {
        var stamp = Card(Guid.NewGuid(), StampCardStatus.Active, stampsRequired: 12);
        var program = Program(stampsRequired: 10);
        var card = Loyalty(requiredStamps: 0);

        LoyaltyCardSnapshot.Apply(card, program, stamp, DateTime.UtcNow);

        Assert.Equal(stamp.Id, card.StampCardId);
        Assert.Equal(12, card.RequiredStamps);
        Assert.Equal(stamp.RulesVersion, card.RulesVersion);
    }

    [Fact]
    public void Apply_NoStampCard_UsesProgramDefaultsAndVersionZero()
    {
        var program = Program(stampsRequired: 10);
        var card = Loyalty(requiredStamps: 0);

        LoyaltyCardSnapshot.Apply(card, program, stampCard: null, DateTime.UtcNow);

        Assert.Null(card.StampCardId);
        Assert.Equal(10, card.RequiredStamps);
        Assert.Equal(0, card.RulesVersion); // legacy program-level binding marker
    }

    [Fact]
    public void Apply_InvalidCardRequirement_FallsBackToProgram()
    {
        var stamp = Card(Guid.NewGuid(), StampCardStatus.Active, stampsRequired: 0);
        var program = Program(stampsRequired: 8);
        var card = Loyalty(requiredStamps: 0);

        LoyaltyCardSnapshot.Apply(card, program, stamp, DateTime.UtcNow);

        Assert.Equal(8, card.RequiredStamps);
        Assert.Equal(stamp.Id, card.StampCardId);
    }

    [Theory]
    [InlineData(0, 1)]     // below the floor → clamped up
    [InlineData(500, 100)] // above the ceiling → clamped down
    [InlineData(25, 25)]   // in range → untouched
    public void Apply_ProgramFallback_IsAlwaysClamped(int programRequired, int expected)
    {
        var card = Loyalty(requiredStamps: 0);
        LoyaltyCardSnapshot.Apply(card, Program(programRequired), null, DateTime.UtcNow);

        Assert.Equal(expected, card.RequiredStamps);
    }

    // ── ResolveDefaultStampCardAsync (binding rule) ────────

    private async Task<Guid> SeedProgramAsync()
    {
        var owner = BookingTestBase.CreateOwner($"snap-{Guid.NewGuid():N}@t.com");
        var business = BookingTestBase.CreateBusiness(owner.Id, "Snapshot Cafe");
        var program = Program(stampsRequired: 10);
        program.BusinessId = business.Id;

        _db.AddRange(owner, business, program);
        await _db.SaveChangesAsync();
        return program.Id;
    }

    private async Task AddCardAsync(Guid programId, StampCardStatus status)
    {
        var stamp = Card(programId, status);
        stamp.BusinessId = (await _db.LoyaltyPrograms.AsNoTracking()
            .FirstAsync(p => p.Id == programId)).BusinessId;
        _db.StampCards.Add(stamp);
        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task Resolve_ExactlyOneActiveCard_BindsIt()
    {
        var programId = await SeedProgramAsync();
        await AddCardAsync(programId, StampCardStatus.Active);
        await AddCardAsync(programId, StampCardStatus.Draft); // non-active never binds

        var bound = await LoyaltyCardSnapshot.ResolveDefaultStampCardAsync(_uow, programId);

        Assert.NotNull(bound);
        Assert.Equal(StampCardStatus.Active, bound!.Status);
    }

    [Fact]
    public async Task Resolve_NoActiveCard_ReturnsNull()
    {
        var programId = await SeedProgramAsync();
        await AddCardAsync(programId, StampCardStatus.Draft);
        await AddCardAsync(programId, StampCardStatus.Archived);

        Assert.Null(await LoyaltyCardSnapshot.ResolveDefaultStampCardAsync(_uow, programId));
    }

    [Fact]
    public async Task Resolve_MultipleActiveCards_ReturnsNull_Ambiguous()
    {
        // Two active cards → no deterministic single answer, so enrollments must
        // stay on program-level defaults (RulesVersion 0) rather than guess.
        var programId = await SeedProgramAsync();
        await AddCardAsync(programId, StampCardStatus.Active);
        await AddCardAsync(programId, StampCardStatus.Active);

        Assert.Null(await LoyaltyCardSnapshot.ResolveDefaultStampCardAsync(_uow, programId));
    }

    [Fact]
    public async Task Resolve_UnknownProgram_ReturnsNull()
    {
        await SeedProgramAsync();

        Assert.Null(await LoyaltyCardSnapshot.ResolveDefaultStampCardAsync(_uow, Guid.NewGuid()));
    }
}
