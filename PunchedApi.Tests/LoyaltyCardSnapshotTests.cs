using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Loyalty;
using PunchedApi.Application.Programs;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
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

    private LoyaltyService CreateLoyaltyService(Mock<ILogger<LoyaltyService>>? logger = null)
    {
        var resolver = new Mock<ICardDesignResolver>();
        resolver.Setup(service => service.ResolveForProgramAsync(It.IsAny<LoyaltyProgram>()))
            .ReturnsAsync(new ResolvedCardDesign { IsDefault = true });
        return new LoyaltyService(_uow, _db, Mock.Of<IStampService>(),
            Mock.Of<IProgramRuleEngine>(), Mock.Of<ICardDesignService>(), resolver.Object,
            logger?.Object ?? TestHelpers.CreateLogger<LoyaltyService>());
    }

    [Fact]
    public async Task Enroll_SelectedPrograms_CreateIndependentCards_AndRejectDuplicateProgram()
    {
        var owner = BookingTestBase.CreateOwner();
        var customer = BookingTestBase.CreateCustomer();
        var business = BookingTestBase.CreateBusiness(owner.Id, "Two program cafe");
        var first = Program(6);
        var second = Program(12);
        foreach (var program in new[] { first, second })
        {
            program.BusinessId = business.Id;
            program.IsActive = true;
            program.Status = ProgramStatus.Active;
        }
        await BookingTestBase.SeedAsync(_db, owner, customer, business, first, second);
        var logger = new Mock<ILogger<LoyaltyService>>();
        var service = CreateLoyaltyService(logger);

        var firstJoin = await service.EnrollAsync(customer.Id, new EnrollCardRequest { BusinessId = business.Id, ProgramId = first.Id });
        var secondJoin = await service.EnrollAsync(customer.Id, new EnrollCardRequest { BusinessId = business.Id, ProgramId = second.Id });
        var repeatedJoin = await service.EnrollAsync(customer.Id, new EnrollCardRequest { BusinessId = business.Id, ProgramId = second.Id });

        Assert.True(firstJoin.Success, string.Join(Environment.NewLine,
            logger.Invocations.Select(invocation => invocation.Arguments[3]?.ToString())));
        Assert.True(secondJoin.Success, string.Join(Environment.NewLine,
            logger.Invocations.Select(invocation => invocation.Arguments[3]?.ToString())));
        Assert.Equal(second.Id, secondJoin.Data!.ProgramId);
        Assert.Equal(12, secondJoin.Data.StampsRequired);
        Assert.Equal("ALREADY_ENROLLED", repeatedJoin.Error?.Code);
        Assert.Equal(2, await _db.LoyaltyCards.CountAsync());
    }

    [Fact]
    public async Task Enroll_UnknownSelectedProgram_DoesNotFallBackToBusinessDefault()
    {
        var programId = await SeedProgramAsync();
        var program = await _db.LoyaltyPrograms.FindAsync(programId);
        program!.IsActive = true;
        program.Status = ProgramStatus.Active;
        var customer = BookingTestBase.CreateCustomer();
        await BookingTestBase.SeedAsync(_db, customer);

        var response = await CreateLoyaltyService().EnrollAsync(customer.Id,
            new EnrollCardRequest { BusinessId = program.BusinessId, ProgramId = Guid.NewGuid() });

        Assert.False(response.Success);
        Assert.Equal("NO_PROGRAM", response.Error?.Code);
        Assert.Empty(await _db.LoyaltyCards.ToListAsync());
    }

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
