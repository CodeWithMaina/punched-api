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
/// GET /businesses/me/customers roster scoping — the Customers tab of a
/// program's details page must list customers of THIS business who also
/// joined THAT program: both predicates must hold (business enrollment AND
/// a loyalty card in the requested program), and a missing program id keeps
/// the endpoint's business-wide behaviour.
/// </summary>
public class BusinessCustomerRosterTests
{
    private sealed record Env(
        ApplicationDbContext Ctx,
        BusinessService Svc,
        Guid OwnerId,
        LoyaltyProgram ProgramA,
        LoyaltyProgram ProgramB,
        User Alice,
        User Bob,
        User Carol);

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
        var alice = AddUser(ctx, "alice@t.com", "Alice", UserRole.Customer);
        var bob = AddUser(ctx, "bob@t.com", "Bob", UserRole.Customer);
        var carol = AddUser(ctx, "carol@t.com", "Carol", UserRole.Customer);
        var dave = AddUser(ctx, "dave@t.com", "Dave", UserRole.Customer);
        var otherOwner = AddUser(ctx, "other-owner@t.com", "Other Owner", UserRole.Business);

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

        var programA = new LoyaltyProgram
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            Name = "Program A",
            StampsRequired = 5,
            RewardValue = 100,
            RewardDescription = "Free coffee",
            CreatedAt = DateTime.UtcNow
        };
        var programB = new LoyaltyProgram
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            Name = "Program B",
            StampsRequired = 6,
            RewardValue = 200,
            RewardDescription = "Free cut",
            CreatedAt = DateTime.UtcNow
        };
        var otherProgram = new LoyaltyProgram
        {
            Id = Guid.NewGuid(),
            BusinessId = otherBusiness.Id,
            Name = "Other Program",
            StampsRequired = 4,
            RewardValue = 50,
            RewardDescription = "Other perk",
            CreatedAt = DateTime.UtcNow
        };
        ctx.LoyaltyPrograms.AddRange(programA, programB, otherProgram);

        // Alice, Bob and Carol are all customers of THIS business;
        // only Alice and Bob joined a program (Carol is cardless).
        ctx.CustomerBusinessEnrollments.AddRange(
            new CustomerBusinessEnrollment { Id = Guid.NewGuid(), CustomerId = alice.Id, BusinessId = business.Id, CreatedAt = DateTime.UtcNow },
            new CustomerBusinessEnrollment { Id = Guid.NewGuid(), CustomerId = bob.Id, BusinessId = business.Id, CreatedAt = DateTime.UtcNow },
            new CustomerBusinessEnrollment { Id = Guid.NewGuid(), CustomerId = carol.Id, BusinessId = business.Id, CreatedAt = DateTime.UtcNow },
            // Dave is a customer of a DIFFERENT business with a card in
            // ITS program — he must never surface in this business's roster.
            new CustomerBusinessEnrollment { Id = Guid.NewGuid(), CustomerId = dave.Id, BusinessId = otherBusiness.Id, CreatedAt = DateTime.UtcNow });

        ctx.LoyaltyCards.AddRange(
            new LoyaltyCard { Id = Guid.NewGuid(), CustomerId = alice.Id, BusinessId = business.Id, ProgramId = programA.Id, CreatedAt = DateTime.UtcNow },
            new LoyaltyCard { Id = Guid.NewGuid(), CustomerId = bob.Id, BusinessId = business.Id, ProgramId = programB.Id, CreatedAt = DateTime.UtcNow },
            new LoyaltyCard { Id = Guid.NewGuid(), CustomerId = dave.Id, BusinessId = otherBusiness.Id, ProgramId = otherProgram.Id, CreatedAt = DateTime.UtcNow });

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

        return new Env(ctx, svc, owner.Id, programA, programB, alice, bob, carol);
    }

    private static Task<PunchedApi.Application.DTOs.ApiResponse<PunchedApi.Application.DTOs.PaginatedResponse<PunchedApi.Application.DTOs.BusinessCustomerResponse>>> QueryAsync(
        Env env, Guid? programId) =>
        env.Svc.GetBusinessCustomersAsync(env.OwnerId, null, null, programId, null, null, "recent", "desc", 1, 25);

    [Fact]
    public async Task Program_Scope_Returns_Only_Business_Customers_Who_Joined_That_Program()
    {
        var env = CreateEnv();
        using (env.Ctx)
        {
            var res = await QueryAsync(env, env.ProgramA.Id);

            Assert.True(res.Success);
            Assert.NotNull(res.Data);
            // Alice joined Program A. Bob (other program), Carol (never
            // joined) and Dave (other business) are all excluded — both
            // predicates must hold for a roster row.
            Assert.Equal(1, res.Data!.TotalCount);
            Assert.Equal(env.Alice.FullName, Assert.Single(res.Data.Items).FullName);
        }
    }

    [Fact]
    public async Task Program_Scope_Follows_The_Requested_Program()
    {
        var env = CreateEnv();
        using (env.Ctx)
        {
            var res = await QueryAsync(env, env.ProgramB.Id);

            Assert.True(res.Success);
            Assert.NotNull(res.Data);
            Assert.Equal(1, res.Data!.TotalCount);
            Assert.Equal(env.Bob.FullName, Assert.Single(res.Data.Items).FullName);
        }
    }

    [Fact]
    public async Task Without_A_Program_Id_The_Roster_Stays_Business_Wide()
    {
        var env = CreateEnv();
        using (env.Ctx)
        {
            var res = await QueryAsync(env, null);

            Assert.True(res.Success);
            Assert.NotNull(res.Data);
            // All three of THIS business's customers (Carol included — she
            // has no program card), and Dave (another business) is absent.
            Assert.Equal(3, res.Data!.TotalCount);
            var names = res.Data.Items.Select(c => c.FullName).ToArray();
            Assert.Equal(
                new[] { env.Alice.FullName, env.Bob.FullName, env.Carol.FullName },
                names.OrderBy(n => n, StringComparer.Ordinal).ToArray());
        }
    }
}
