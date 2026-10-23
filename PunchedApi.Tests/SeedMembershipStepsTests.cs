using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using PunchedApi.Application.Settings;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;
using PunchedApi.Infrastructure.Data.Seeding;
using PunchedApi.Infrastructure.Data.Seeding.Steps;

namespace PunchedApi.Tests;

/// <summary>
/// Seed steps must produce the same canonical rows production flows do: an
/// <c>Active Owner</c> membership per business and an <c>Active Staff</c> membership
/// per staff link. They are also re-runnable (<c>db seed</c> is idempotent), which the
/// DDL's unique <c>(user_id, business_id)</c> index would otherwise reject.
/// </summary>
public class SeedMembershipStepsTests : IDisposable
{
    private const string OwnerKey = "owner";
    private const string StaffKey = "staff";
    private const string BusinessKey = "biz";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public SeedMembershipStepsTests()
    {
        _connection = BookingTestBase.CreateConnection();
        _context = BookingTestBase.CreateContext(_connection);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private static SeedExecutionContext CreateExecutionContext(ApplicationDbContext db)
    {
        var scenario = new SeedScenario(
            new List<SeedBusinessDefinition>
            {
                new(
                    Key: BusinessKey,
                    Name: "Seed Salon",
                    Category: "salon",
                    Location: "Nairobi",
                    PhoneNumber: null,
                    Email: null,
                    Description: null,
                    LogoUrl: null,
                    MpesaNumber: "254700000000",
                    OwnerUserKey: OwnerKey,
                    CreatedAt: DateTime.UtcNow,
                    ProgramNames: Array.Empty<string>(),
                    StampsRequired: 5,
                    RewardValue: 500m,
                    RewardDescription: "Free cut",
                    RewardExpirationHours: 24,
                    ReferralRewardType: ReferralRewardType.Stamp,
                    ReferralRewardValue: 50m,
                    ReferralsRequired: 3,
                    ReferralExpirationDays: 30,
                    StaffUserKeys: new[] { StaffKey },
                    CustomerUserKeys: Array.Empty<string>(),
                    IsFocusBusiness: false)
            },
            new List<SeedUserDefinition>());

        return new SeedExecutionContext
        {
            Db = db,
            Environment = new Mock<IWebHostEnvironment>().Object,
            Logger = NullLogger.Instance,
            Random = new SeedRandom(Options.Create(new SeedOptions { RandomSeed = 42 })),
            ReportOutputPath = Path.Combine(Path.GetTempPath(), "seed-report.json"),
            Report = new SeedReport { Mode = SeedExecutionMode.Idempotent },
            Scenario = scenario,
            ServiceProvider = new Mock<IServiceProvider>().Object
        };
    }

    private async Task<SeedExecutionContext> SeedUsersAsync()
    {
        var context = CreateExecutionContext(_context);
        var owner = BookingTestBase.CreateOwner("seed-owner@test.com");
        var staff = BookingTestBase.CreateStaffWithoutMembership("seed-staff@test.com");
        await BookingTestBase.SeedAsync(_context, owner, staff);

        context.UsersByKey[OwnerKey] = owner;
        context.UsersByKey[StaffKey] = staff;
        return context;
    }

    [Fact]
    public async Task BusinessSeedStep_CreatesTheOwnerMembership_AndRepairsOnReseed()
    {
        var context = await SeedUsersAsync();
        var step = new BusinessSeedStep();

        await step.ExecuteAsync(context, CancellationToken.None);

        var business = context.BusinessesByKey[BusinessKey];
        var owner = context.UsersByKey[OwnerKey];
        var membership = await _context.BusinessMemberships
            .SingleAsync(m => m.BusinessId == business.Id);
        Assert.Equal(owner.Id, membership.UserId);
        Assert.Equal(BusinessMembershipRole.Owner, membership.Role);
        Assert.Equal(BusinessMembershipStatus.Active, membership.Status);
        Assert.Equal(owner.Id, business.OwnerId);
        Assert.Equal(1, context.Report.Counts["OwnerMembershipsCreated"]);

        // Damage the row the way a manual edit or an old build could, then reseed:
        // the step must normalize it back to an Active Owner — not insert a second
        // row (which the unique pair index would reject) and not leave the tenant
        // owned only by a pointer.
        membership.Role = BusinessMembershipRole.Staff;
        membership.Status = BusinessMembershipStatus.Suspended;
        await _context.SaveChangesAsync();

        await step.ExecuteAsync(context, CancellationToken.None);

        var repaired = await _context.BusinessMemberships
            .SingleAsync(m => m.BusinessId == business.Id);
        Assert.Equal(BusinessMembershipRole.Owner, repaired.Role);
        Assert.Equal(BusinessMembershipStatus.Active, repaired.Status);
        Assert.Null(repaired.EndedAt);
        Assert.Equal(0, context.Report.Counts["OwnerMembershipsCreated"]);
        Assert.Equal(1, await _context.Businesses.CountAsync());
    }

    [Fact]
    public async Task StaffLinkSeedStep_IsIdempotent_AndReactivates()
    {
        var context = await SeedUsersAsync();
        await new BusinessSeedStep().ExecuteAsync(context, CancellationToken.None);

        var business = context.BusinessesByKey[BusinessKey];
        var staff = context.UsersByKey[StaffKey];
        var step = new StaffLinkSeedStep();

        await step.ExecuteAsync(context, CancellationToken.None);
        await step.ExecuteAsync(context, CancellationToken.None);

        var row = await _context.BusinessMemberships
            .SingleAsync(m => m.BusinessId == business.Id && m.UserId == staff.Id);
        Assert.Equal(BusinessMembershipRole.Staff, row.Role);
        Assert.Equal(BusinessMembershipStatus.Active, row.Status);
        Assert.Equal(1, context.Report.Counts["StaffLinked"]);

        row.Status = BusinessMembershipStatus.Suspended;
        row.EndedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        await step.ExecuteAsync(context, CancellationToken.None);

        var reactivated = await _context.BusinessMemberships
            .SingleAsync(m => m.BusinessId == business.Id && m.UserId == staff.Id);
        Assert.Equal(BusinessMembershipStatus.Active, reactivated.Status);
        Assert.Null(reactivated.EndedAt);
        Assert.Equal(1, context.Report.Counts["StaffLinked"]);
    }
}
