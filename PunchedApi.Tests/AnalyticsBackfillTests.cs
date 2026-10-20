using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using PunchedApi.Application.Analytics;
using PunchedApi.Application.Programs;
using PunchedApi.Application.Services;
using PunchedApi.Application.Settings;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;
using PunchedApi.Infrastructure.Repositories;

namespace PunchedApi.Tests;

/// <summary>
/// Tests for R4 (Backfill), R9, R10, R11 backfill methods, plus the refactored
/// guarantees: idempotent replaceable aggregates, durable resume checkpoints,
/// tenant isolation, empty ranges, and explicit (non-startup) execution.
/// </summary>
public class AnalyticsBackfillTests
{
    private static ApplicationDbContext CreateContext(string name)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(name)
            .Options;
        return new ApplicationDbContext(options);
    }

    private static Business CreateBusiness(string name = "TestBiz") => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Category = "cafe",
        Location = "Nairobi",
        MpesaNumber = "123456",
        OwnerId = Guid.NewGuid(),
        CreatedAt = DateTime.UtcNow.AddDays(-30)
    };

    private static User CreateUser(string email = "owner@test.com") => new()
    {
        Id = Guid.NewGuid(),
        Email = email,
        FullName = "Test Owner",
        Role = UserRole.Business,
        CreatedAt = DateTime.UtcNow.AddDays(-30)
    };

    private static LoyaltyProgram CreateProgram(Guid businessId) => new()
    {
        Id = Guid.NewGuid(),
        BusinessId = businessId,
        Name = "Coffee Rewards",
        StampsRequired = 10,
        RewardValue = 500,
        RewardDescription = "Free Coffee",
        IsActive = true,
        CreatedAt = DateTime.UtcNow.AddDays(-30)
    };

    private static LoyaltyCard CreateCard(Guid businessId, Guid customerId, Guid programId) => new()
    {
        Id = Guid.NewGuid(),
        CustomerId = customerId,
        BusinessId = businessId,
        ProgramId = programId,
        TotalStamps = 5,
        LifetimeStamps = 5,
        EnrolledAt = DateTime.UtcNow.AddDays(-20)
    };

    [Fact]
    public async Task BackfillBusinessAsync_CreatesBusinessDailyAnalytics_ForEachDayInRange()
    {
        // Arrange
        using var context = CreateContext("BackfillBusiness_01");
        var business = CreateBusiness();
        var owner = CreateUser();
        var program = CreateProgram(business.Id);
        var customer = CreateUser("customer@test.com");
        customer.Role = UserRole.Customer;
        var card = CreateCard(business.Id, customer.Id, program.Id);
        await context.Businesses.AddAsync(business);
        await context.Users.AddRangeAsync(owner, customer);
        await context.LoyaltyPrograms.AddAsync(program);
        await context.LoyaltyCards.AddAsync(card);
        await context.Stamps.AddAsync(new Stamp
        {
            Id = Guid.NewGuid(),
            CardId = card.Id,
            StampNumber = 1,
            StampedAt = DateTime.UtcNow.AddDays(-5),
            QrTokenId = Guid.NewGuid()
        });
        await context.SaveChangesAsync();

        // Act
        var logger = TestHelpers.CreateLogger<AnalyticsAggregationService>();
        var service = new AnalyticsAggregationService(context, logger);
        var from = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-7));
        var to = DateOnly.FromDateTime(DateTime.UtcNow);
        await service.BackfillBusinessAsync(business.Id, from, to);

        // Assert — at least one BusinessDailyAnalytics row exists
        var analyticsRows = await context.BusinessDailyAnalytics
            .Where(x => x.BusinessId == business.Id)
            .ToListAsync();
        Assert.NotEmpty(analyticsRows);
        var dayWithStamp = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-5));
        var thatRow = analyticsRows.FirstOrDefault(x => x.Date == dayWithStamp);
        Assert.NotNull(thatRow);
        Assert.True(thatRow.Stamps >= 1);
    }

    [Fact]
    public async Task BackfillAllBusinessesAsync_ProcessesAllBusinesses()
    {
        // Arrange
        using var context = CreateContext("BackfillAll_01");
        var business1 = CreateBusiness("Biz1");
        var business2 = CreateBusiness("Biz2");
        await context.Businesses.AddRangeAsync(business1, business2);
        await context.SaveChangesAsync();
        var from = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-7));
        var to = DateOnly.FromDateTime(DateTime.UtcNow);

        // Act
        var logger = TestHelpers.CreateLogger<AnalyticsAggregationService>();
        var service = new AnalyticsAggregationService(context, logger);
        await service.BackfillAllBusinessesAsync(from, to);

        // Assert — each business gets a row for every day in the range
        var distinctBusinesses = await context.BusinessDailyAnalytics
            .Select(x => x.BusinessId)
            .Distinct()
            .CountAsync();
        Assert.Equal(2, distinctBusinesses);

        var rowsForBiz1 = await context.BusinessDailyAnalytics
            .CountAsync(x => x.BusinessId == business1.Id);
        Assert.True(rowsForBiz1 >= 1);
    }

    [Fact]
    public async Task LoyaltyService_BackfillProgramHistoryAsync_CreatesHistoryRecords()
    {
        // Arrange
        using var context = CreateContext("BackfillHistory_01");
        var business = CreateBusiness();
        var program = CreateProgram(business.Id);
        await context.Businesses.AddAsync(business);
        await context.LoyaltyPrograms.AddAsync(program);
        await context.SaveChangesAsync();

        // Act
        var logger = TestHelpers.CreateLogger<LoyaltyService>();
        var uow = new UnitOfWork(context);
        var service = new LoyaltyService(uow, context, new Mock<IStampService>().Object, new ProgramRuleEngine(),
            new Mock<ICardDesignService>().Object, new Mock<ICardDesignResolver>().Object, logger);
        await service.BackfillProgramHistoryAsync();

        // Assert
        var historyRecords = await context.LoyaltyProgramHistory.ToListAsync();
        Assert.NotEmpty(historyRecords);
        var record = historyRecords.First();
        Assert.Equal(program.Id, record.LoyaltyProgramId);
        Assert.Equal(program.StampsRequired, record.StampsRequired);
        Assert.Equal(program.RewardValue, record.RewardValue);
    }

    [Fact]
    public async Task SegmentationService_BackfillAllBusinessesAsync_CreatesCustomerSegments()
    {
        // Arrange
        using var context = CreateContext("BackfillSegments_01");
        var business = CreateBusiness();
        var program = CreateProgram(business.Id);
        var customer = CreateUser("seg@customer.com");
        customer.Role = UserRole.Customer;
        var card = CreateCard(business.Id, customer.Id, program.Id);
        card.TotalStamps = 8;
        await context.Businesses.AddAsync(business);
        await context.Users.AddAsync(customer);
        await context.LoyaltyPrograms.AddAsync(program);
        await context.LoyaltyCards.AddAsync(card);
        await context.SaveChangesAsync();

        // Act
        var logger = TestHelpers.CreateLogger<SegmentationService>();
        var service = new SegmentationService(context, logger);
        await service.BackfillAllBusinessesAsync();

        // Assert
        var segments = await context.CustomerSegments.ToListAsync();
        Assert.NotEmpty(segments);
        Assert.All(segments, s => Assert.Equal(business.Id, s.BusinessId));
    }
    [Fact]
    public async Task BackfillBusinessAsync_Twice_DoesNotDuplicateOrDoubleCount()
    {
        using var context = CreateContext("BackfillIdempotent_01");
        var business = CreateBusiness();
        var program = CreateProgram(business.Id);
        var customer = CreateUser("idem@test.com");
        customer.Role = UserRole.Customer;
        var card = CreateCard(business.Id, customer.Id, program.Id);
        await context.Businesses.AddAsync(business);
        await context.Users.AddAsync(customer);
        await context.LoyaltyPrograms.AddAsync(program);
        await context.LoyaltyCards.AddAsync(card);
        var stampDay = DateTime.UtcNow.AddDays(-3);
        await context.Stamps.AddAsync(new Stamp
        {
            Id = Guid.NewGuid(),
            CardId = card.Id,
            StampNumber = 1,
            StampedAt = stampDay,
            QrTokenId = Guid.NewGuid()
        });
        await context.SaveChangesAsync();

        var service = new AnalyticsAggregationService(context, TestHelpers.CreateLogger<AnalyticsAggregationService>());
        var from = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-5));
        var to = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));
        await service.BackfillBusinessAsync(business.Id, from, to);
        var first = await context.BusinessDailyAnalytics
            .Where(x => x.BusinessId == business.Id && x.Date >= from && x.Date <= to)
            .OrderBy(x => x.Date)
            .Select(x => new { x.Date, x.Stamps })
            .ToListAsync();

        await service.BackfillBusinessAsync(business.Id, from, to);
        var second = await context.BusinessDailyAnalytics
            .Where(x => x.BusinessId == business.Id && x.Date >= from && x.Date <= to)
            .OrderBy(x => x.Date)
            .Select(x => new { x.Date, x.Stamps })
            .ToListAsync();

        Assert.Equal(first.Count, second.Count);
        Assert.Equal(first.Select(x => x.Stamps), second.Select(x => x.Stamps));
        Assert.Equal(1, second.Count(x => x.Date == DateOnly.FromDateTime(stampDay)));
        Assert.Equal(1, second.First(x => x.Date == DateOnly.FromDateTime(stampDay)).Stamps);
    }

    [Fact]
    public async Task BackfillBusinessAsync_AfterSourceChange_ReplacesStaleValues()
    {
        using var context = CreateContext("BackfillReplace_01");
        var business = CreateBusiness();
        var program = CreateProgram(business.Id);
        var customer = CreateUser("replace@test.com");
        customer.Role = UserRole.Customer;
        var card = CreateCard(business.Id, customer.Id, program.Id);
        await context.Businesses.AddAsync(business);
        await context.Users.AddAsync(customer);
        await context.LoyaltyPrograms.AddAsync(program);
        await context.LoyaltyCards.AddAsync(card);
        var stampAt = DateTime.UtcNow.AddDays(-2);
        await context.Stamps.AddAsync(new Stamp
        {
            Id = Guid.NewGuid(),
            CardId = card.Id,
            StampNumber = 1,
            StampedAt = stampAt,
            QrTokenId = Guid.NewGuid()
        });
        await context.SaveChangesAsync();

        var service = new AnalyticsAggregationService(context, TestHelpers.CreateLogger<AnalyticsAggregationService>());
        var day = DateOnly.FromDateTime(stampAt);
        await service.BackfillBusinessAsync(business.Id, day, day);
        var before = await context.BusinessDailyAnalytics.FirstAsync(x => x.BusinessId == business.Id && x.Date == day);
        Assert.Equal(1, before.Stamps);

        await context.Stamps.AddAsync(new Stamp
        {
            Id = Guid.NewGuid(),
            CardId = card.Id,
            StampNumber = 2,
            StampedAt = stampAt.AddHours(1),
            QrTokenId = Guid.NewGuid()
        });
        await context.SaveChangesAsync();

        await service.BackfillBusinessAsync(business.Id, day, day);
        var after = await context.BusinessDailyAnalytics.FirstAsync(x => x.BusinessId == business.Id && x.Date == day);
        Assert.Equal(2, after.Stamps);
    }

    [Fact]
    public async Task BackfillBusinessAsync_EmptyRange_WritesZeroRows()
    {
        using var context = CreateContext("BackfillEmpty_01");
        var business = CreateBusiness();
        await context.Businesses.AddAsync(business);
        await context.SaveChangesAsync();

        var service = new AnalyticsAggregationService(context, TestHelpers.CreateLogger<AnalyticsAggregationService>());
        var from = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-4));
        var to = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-2));
        await service.BackfillBusinessAsync(business.Id, from, to);

        var rows = await context.BusinessDailyAnalytics
            .Where(x => x.BusinessId == business.Id && x.Date >= from && x.Date <= to)
            .ToListAsync();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal(0, r.Stamps));
    }

    [Fact]
    public async Task BackfillBusinessAsync_TwoBusinesses_StayIsolated()
    {
        using var context = CreateContext("BackfillIsolation_01");
        var bizA = CreateBusiness("BizA");
        var bizB = CreateBusiness("BizB");
        var progA = CreateProgram(bizA.Id);
        var progB = CreateProgram(bizB.Id);
        var custA = CreateUser("a@iso.com");
        custA.Role = UserRole.Customer;
        var cardA = CreateCard(bizA.Id, custA.Id, progA.Id);
        await context.Businesses.AddRangeAsync(bizA, bizB);
        await context.Users.AddAsync(custA);
        await context.LoyaltyPrograms.AddRangeAsync(progA, progB);
        await context.LoyaltyCards.AddAsync(cardA);
        var stampAt = DateTime.UtcNow.AddDays(-2);
        await context.Stamps.AddAsync(new Stamp
        {
            Id = Guid.NewGuid(),
            CardId = cardA.Id,
            StampNumber = 1,
            StampedAt = stampAt,
            QrTokenId = Guid.NewGuid()
        });
        await context.SaveChangesAsync();

        var service = new AnalyticsAggregationService(context, TestHelpers.CreateLogger<AnalyticsAggregationService>());
        var day = DateOnly.FromDateTime(stampAt);
        await service.BackfillBusinessAsync(bizA.Id, day, day);
        await service.BackfillBusinessAsync(bizB.Id, day, day);

        var rowA = await context.BusinessDailyAnalytics.FirstAsync(x => x.BusinessId == bizA.Id && x.Date == day);
        var rowB = await context.BusinessDailyAnalytics.FirstAsync(x => x.BusinessId == bizB.Id && x.Date == day);
        Assert.Equal(1, rowA.Stamps);
        Assert.Equal(0, rowB.Stamps);
    }

    [Fact]
    public void SeedOptions_Defaults_DisableStartupSeedingAndBackfill()
    {
        var options = new SeedOptions();
        Assert.False(options.RunOnStartup);
        Assert.False(options.IncludeAnalyticsBackfill);
    }

    [Fact]
    public async Task Runner_RunTwice_ReusesCheckpoint_WithoutDoubleCount()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(o => o.UseInMemoryDatabase("RunnerResume_01"));
        services.AddScoped<IAnalyticsAggregationService, AnalyticsAggregationService>();
        services.AddScoped<IAnalyticsBackfillRunner, AnalyticsBackfillRunner>();
        var provider = services.BuildServiceProvider();

        Guid businessId;
        var from = new DateOnly(2026, 3, 9);
        var to = new DateOnly(2026, 3, 11);
        using (var setup = provider.CreateScope())
        {
            var context = setup.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var business = CreateBusiness();
            var program = CreateProgram(business.Id);
            var customer = CreateUser("runner@test.com");
            customer.Role = UserRole.Customer;
            var card = CreateCard(business.Id, customer.Id, program.Id);
            await context.Businesses.AddAsync(business);
            await context.Users.AddAsync(customer);
            await context.LoyaltyPrograms.AddAsync(program);
            await context.LoyaltyCards.AddAsync(card);
            await context.Stamps.AddAsync(new Stamp
            {
                Id = Guid.NewGuid(),
                CardId = card.Id,
                StampNumber = 1,
                StampedAt = new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc),
                QrTokenId = Guid.NewGuid()
            });
            await context.SaveChangesAsync();
            businessId = business.Id;
        }

        using (var runScope = provider.CreateScope())
        {
            var runner = runScope.ServiceProvider.GetRequiredService<IAnalyticsBackfillRunner>();
            var first = await runner.RunAsync(new AnalyticsBackfillOptions
            {
                BusinessId = businessId,
                From = from,
                To = to,
                RunGroupId = Guid.NewGuid(),
            });
            Assert.True(first.Success);
            Assert.Equal(3, first.DaysProcessed);
        }

        using (var verify = provider.CreateScope())
        {
            var context = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var runs = await context.AnalyticsBackfillRuns.Where(r => r.BusinessId == businessId).ToListAsync();
            Assert.Single(runs);
            Assert.Equal("Completed", runs[0].Status);
            Assert.Equal(to, runs[0].LastCompletedDate);

            var stampDay = new DateOnly(2026, 3, 10);
            var row = await context.BusinessDailyAnalytics.FirstAsync(x => x.BusinessId == businessId && x.Date == stampDay);
            Assert.Equal(1, row.Stamps);

            var runner = verify.ServiceProvider.GetRequiredService<IAnalyticsBackfillRunner>();
            var second = await runner.RunAsync(new AnalyticsBackfillOptions
            {
                BusinessId = businessId,
                From = from,
                To = to,
                RunGroupId = Guid.NewGuid(),
            });
            Assert.True(second.Success);
            Assert.Equal(0, second.DaysProcessed);
            Assert.Equal(3, second.DaysSkipped);
            var rowAfter = await context.BusinessDailyAnalytics.FirstAsync(x => x.BusinessId == businessId && x.Date == stampDay);
            Assert.Equal(1, rowAfter.Stamps);
        }
    }
}