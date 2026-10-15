using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;
using PunchedApi.Infrastructure.Repositories;

namespace PunchedApi.Tests;

/// <summary>
/// GET /businesses/me/customers/{id}/details — the single aggregated request
/// that backs the initial customer-details screen. Asserts the response
/// contract (identity + status + metrics + loyalty summary), business/customer
/// tenant isolation, the not-found path, and loyalty resolution.
/// </summary>
public class CustomerDetailsAggregationTests
{
    private sealed record Env(
        ApplicationDbContext Ctx,
        BusinessService Svc,
        Guid OwnerId,
        Business Business,
        LoyaltyProgram Program,
        User Alice,
        User Bob,
        User Dave);

    private static User AddUser(ApplicationDbContext ctx, string email, string fullName, UserRole role)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            FullName = fullName,
            Role = role,
            CreatedAt = DateTime.UtcNow,
            Auth = new UserAuth
            {
                Id = Guid.NewGuid(),
                Email = email,
                PasswordHash = "x",
                IsVerified = true,
                CreatedAt = DateTime.UtcNow
            }
        };
        ctx.Users.Add(user);
        return user;
    }

    private static Env CreateEnv()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(conn).Options;
        var ctx = new ApplicationDbContext(options);
        ctx.Database.EnsureCreated();

        var owner = AddUser(ctx, "owner@t.com", "Owner", UserRole.Business);
        var otherOwner = AddUser(ctx, "other-owner@t.com", "Other Owner", UserRole.Business);
        var alice = AddUser(ctx, "alice@t.com", "Alice Ngugi", UserRole.Customer);
        var bob = AddUser(ctx, "bob@t.com", "Bob Otieno", UserRole.Customer);
        var dave = AddUser(ctx, "dave@t.com", "Dave Kariuki", UserRole.Customer);

        var business = new Business
        {
            Id = Guid.NewGuid(),
            Name = "Kudu Brew Bar",
            Category = "cafe",
            Location = "Nairobi",
            MpesaNumber = "1",
            OwnerId = owner.Id,
            TimeZoneId = "UTC",
            CreatedAt = DateTime.UtcNow
        };
        var otherBusiness = new Business
        {
            Id = Guid.NewGuid(),
            Name = "Other Biz",
            Category = "salon",
            Location = "Nairobi",
            MpesaNumber = "1",
            OwnerId = otherOwner.Id,
            TimeZoneId = "UTC",
            CreatedAt = DateTime.UtcNow
        };
        ctx.Businesses.AddRange(business, otherBusiness);

        var program = new LoyaltyProgram
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            Name = "Brew Rewards",
            StampsRequired = 8,
            RewardDescription = "Free coffee",
            RewardValue = 500,
            RewardExpirationHours = 72,
            CreatedAt = DateTime.UtcNow
        };
        var otherProgram = new LoyaltyProgram
        {
            Id = Guid.NewGuid(),
            BusinessId = otherBusiness.Id,
            Name = "Salon Points",
            StampsRequired = 10,
            RewardDescription = "Free trim",
            CreatedAt = DateTime.UtcNow
        };
        ctx.LoyaltyPrograms.AddRange(program, otherProgram);

        ctx.CustomerBusinessEnrollments.AddRange(
            new CustomerBusinessEnrollment { Id = Guid.NewGuid(), CustomerId = alice.Id, BusinessId = business.Id, Status = CustomerBusinessEnrollmentStatus.Active, CreatedAt = DateTime.UtcNow.AddDays(-30) },
            new CustomerBusinessEnrollment { Id = Guid.NewGuid(), CustomerId = bob.Id, BusinessId = business.Id, Status = CustomerBusinessEnrollmentStatus.Active, CreatedAt = DateTime.UtcNow.AddDays(-10) },
            new CustomerBusinessEnrollment { Id = Guid.NewGuid(), CustomerId = dave.Id, BusinessId = otherBusiness.Id, Status = CustomerBusinessEnrollmentStatus.Active, CreatedAt = DateTime.UtcNow.AddDays(-5) });

        var aliceCard = new LoyaltyCard
        {
            Id = Guid.NewGuid(),
            CustomerId = alice.Id,
            BusinessId = business.Id,
            ProgramId = program.Id,
            TotalStamps = 3,
            LifetimeStamps = 9,
            TotalRedemptions = 1,
            RequiredStamps = 8,
            CreatedAt = DateTime.UtcNow.AddDays(-30)
        };
        ctx.LoyaltyCards.AddRange(aliceCard, new LoyaltyCard
        {
            Id = Guid.NewGuid(),
            CustomerId = dave.Id,
            BusinessId = otherBusiness.Id,
            ProgramId = otherProgram.Id,
            TotalStamps = 2,
            LifetimeStamps = 2,
            RequiredStamps = 10,
            CreatedAt = DateTime.UtcNow.AddDays(-5)
        });

        ctx.Stamps.AddRange(
            new Stamp { Id = Guid.NewGuid(), CardId = aliceCard.Id, StampNumber = 1, StampedAt = DateTime.UtcNow.AddDays(-20), CreatedAt = DateTime.UtcNow.AddDays(-20) },
            new Stamp { Id = Guid.NewGuid(), CardId = aliceCard.Id, StampNumber = 2, StampedAt = DateTime.UtcNow.AddDays(-2), CreatedAt = DateTime.UtcNow.AddDays(-2) });

        ctx.SaveChanges();

        var scope = new Mock<IBusinessScopeResolver>();
        scope.Setup(s => s.GetOwnedBusinessIdAsync(owner.Id)).ReturnsAsync(business.Id);

        var svc = new BusinessService(
            new UnitOfWork(ctx),
            ctx,
            new Mock<IInsightService>().Object,
            scope.Object,
            new Mock<ISubscriptionProvisioningService>().Object,
            new Mock<IModuleEntitlementService>().Object,
            new Mock<IBusinessSlugGenerator>().Object,
            TestHelpers.CreateLogger<BusinessService>());

        return new Env(ctx, svc, owner.Id, business, program, alice, bob, dave);
    }

    [Fact]
    public async Task Details_Return_Identity_Status_Metrics_And_Loyalty_In_One_Payload()
    {
        var e = CreateEnv();

        var res = await e.Svc.GetCustomerDetailsAsync(e.OwnerId, e.Alice.Id);

        Assert.True(res.Success);
        var d = res.Data!;
        Assert.Equal(e.Alice.Id, d.Customer.UserId);
        Assert.Equal("Alice Ngugi", d.Customer.FullName);
        Assert.Equal("active", d.Status.EnrollmentStatus);
        Assert.True(d.Status.HasLoyaltyCard);

        Assert.Equal(9, d.Metrics.LifetimeStamps);
        Assert.Equal(1, d.Metrics.StampsIn7d);
        Assert.Equal(1, d.Metrics.TotalRedemptions);
        Assert.Equal(2, d.Metrics.TotalVisits);

        Assert.NotNull(d.Loyalty);
        Assert.Equal("Brew Rewards", d.Loyalty.ProgramName);
        Assert.Equal(3, d.Loyalty.TotalStamps);
        Assert.Equal(8, d.Loyalty.StampsRequired);
        Assert.False(d.Loyalty.RewardReady);
        Assert.True(d.ServerMs >= 0);
    }

    [Fact]
    public async Task Details_Are_Scoped_To_The_Owners_Business()
    {
        var e = CreateEnv();

        // Dave is enrolled in another business, so the owner's scope resolver
        // yields no enrollment for (owner business, Dave) -> NOT_FOUND.
        var res = await e.Svc.GetCustomerDetailsAsync(e.OwnerId, e.Dave.Id);

        Assert.False(res.Success);
        Assert.Null(res.Data);
        Assert.Equal("NOT_FOUND", res.Error!.Code);
    }

    [Fact]
    public async Task Details_For_Unknown_Customer_Return_Not_Found()
    {
        var e = CreateEnv();

        var res = await e.Svc.GetCustomerDetailsAsync(e.OwnerId, Guid.NewGuid());

        Assert.False(res.Success);
        Assert.Equal("NOT_FOUND", res.Error!.Code);
    }

    [Fact]
    public async Task Cardless_Customer_Returns_Null_Loyalty_But_Valid_Identity()
    {
        var e = CreateEnv();

        // Bob is enrolled but has no loyalty card yet: identity/status must
        // still resolve while the loyalty summary stays null.
        var res = await e.Svc.GetCustomerDetailsAsync(e.OwnerId, e.Bob.Id);

        Assert.True(res.Success);
        var d = res.Data!;
        Assert.Equal(e.Bob.Id, d.Customer.UserId);
        Assert.Equal("Bob Otieno", d.Customer.FullName);
        Assert.Equal("active", d.Status.EnrollmentStatus);
        Assert.False(d.Status.HasLoyaltyCard);
        Assert.Null(d.Loyalty);
        Assert.Equal(0, d.Metrics.LifetimeStamps);
        Assert.Equal(0, d.Metrics.StampsIn7d);
    }

    [Fact]
    public async Task Reward_Ready_Is_Derived_When_Stamps_Reach_Target()
    {
        var e = CreateEnv();

        var card = e.Ctx.LoyaltyCards.First(c => c.CustomerId == e.Alice.Id);
        card.TotalStamps = 8; // reaches the program's StampsRequired of 8
        e.Ctx.LoyaltyCards.Update(card);
        await e.Ctx.SaveChangesAsync();

        var res = await e.Svc.GetCustomerDetailsAsync(e.OwnerId, e.Alice.Id);

        Assert.True(res.Success);
        Assert.NotNull(res.Data!.Loyalty);
        Assert.Equal(8, res.Data.Loyalty.StampsRequired);
        Assert.Equal(8, res.Data.Loyalty.TotalStamps);
        Assert.True(res.Data.Loyalty.RewardReady);
    }
}