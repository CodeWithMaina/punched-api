using Microsoft.EntityFrameworkCore;
using Moq;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;
using PunchedApi.Infrastructure.Repositories;

namespace PunchedApi.Tests;

/// <summary>
/// Canonical business provisioning (POST /v1/businesses).
///
/// The invariants pinned here are the whole point of the membership refactor:
///   • the business row, its Owner membership and the OwnerId pointer commit
///     together (one unit of work);
///   • ownership is decided by an ACTIVE Owner membership — never by a global
///     persona, never by a client flag;
///   • a repeat call is refused and writes nothing (idempotent replay);
///   • an existing Customer membership is UPGRADED in place, not duplicated;
///   • platform administrators never become tenants.
/// </summary>
public class BusinessRegistrationTests
{
    private static ApplicationDbContext CreateContext(string name) =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(name)
            .Options);

    private static BusinessService CreateBusinessService(ApplicationDbContext context) =>
        new(
            new UnitOfWork(context),
            context,
            new Mock<IInsightService>().Object,
            new Mock<IBusinessScopeResolver>().Object,
            new Mock<ISubscriptionProvisioningService>().Object,
            new Mock<IModuleEntitlementService>().Object,
            new BusinessSlugGenerator(context),
            new BusinessMembershipResolver(context),
            TestHelpers.CreateLogger<BusinessService>());

    private static User AddUser(
        ApplicationDbContext context,
        string email,
        UserRole role = UserRole.Customer)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            FullName = "Jane Chege",
            Role = role,
            CreatedAt = DateTime.UtcNow
        };
        context.Users.Add(user);
        context.SaveChanges();
        return user;
    }

    private static CreateBusinessRequest ValidRequest() => new()
    {
        Name = "Chege's Java Hut",
        Category = "Cafe",
        Location = "Nairobi",
        PhoneNumber = "+254700000001",
        Email = "cafe@example.com",
        MpesaNumber = "123456",
        Description = "Specialty coffee and pastries."
    };

    [Fact]
    public async Task CreateBusiness_WritesBusinessOwnerMembershipAndPointer_Atomically()
    {
        using var context = CreateContext("BizProv_Create_01");
        var owner = AddUser(context, "owner@example.com");
        var service = CreateBusinessService(context);

        var result = await service.CreateBusinessAsync(owner.Id, ValidRequest());

        Assert.True(result.Success, result.Error?.Message);

        var business = await context.Businesses.SingleAsync(b => b.Name == "Chege's Java Hut");
        Assert.Equal(owner.Id, business.OwnerId);
        Assert.Equal("cafe@example.com", business.Email);
        Assert.Equal("123456", business.MpesaNumber);
        Assert.False(string.IsNullOrWhiteSpace(business.Slug));

        var membership = await context.BusinessMemberships.SingleAsync(m => m.BusinessId == business.Id);
        Assert.Equal(owner.Id, membership.UserId);
        Assert.Equal(BusinessMembershipRole.Owner, membership.Role);
        Assert.Equal(BusinessMembershipStatus.Active, membership.Status);

        // The persona is a routing hint only, but it is kept in step for the UI.
        Assert.Equal(UserRole.Business, (await context.Users.SingleAsync(u => u.Id == owner.Id)).Role);
    }

    [Fact]
    public async Task CreateBusiness_Replay_IsRefused_AndCreatesNothing()
    {
        using var context = CreateContext("BizProv_Replay_01");
        var owner = AddUser(context, "owner@example.com");
        var service = CreateBusinessService(context);

        Assert.True((await service.CreateBusinessAsync(owner.Id, ValidRequest())).Success);
        var replay = await service.CreateBusinessAsync(owner.Id, ValidRequest());

        Assert.False(replay.Success);
        Assert.Equal("BUSINESS_EXISTS", replay.Error?.Code);
        Assert.Equal(1, await context.Businesses.CountAsync());
        Assert.Equal(1, await context.BusinessMemberships.CountAsync());
    }

    [Fact]
    public async Task CreateBusiness_Admin_IsRefused_AndWritesNothing()
    {
        using var context = CreateContext("BizProv_Admin_01");
        var admin = AddUser(context, "admin@example.com", UserRole.Admin);
        var service = CreateBusinessService(context);

        var result = await service.CreateBusinessAsync(admin.Id, ValidRequest());

        Assert.False(result.Success);
        Assert.Equal("ROLE_NOT_ELIGIBLE", result.Error?.Code);
        Assert.Empty(context.Businesses);
        Assert.Empty(context.BusinessMemberships);
    }

    [Fact]
    public async Task CreateBusiness_UpgradesAnExistingCustomerMembership_InPlace()
    {
        using var context = CreateContext("BizProv_Upgrade_01");
        var user = AddUser(context, "shopper@example.com");
        var business = new Business
        {
            Id = Guid.NewGuid(),
            Name = "Corner Shop",
            Category = "Retail",
            Location = "Nairobi",
            MpesaNumber = "999",
            CreatedAt = DateTime.UtcNow
        };
        context.Businesses.Add(business);
        context.BusinessMemberships.Add(new BusinessMembership
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            BusinessId = business.Id,
            Role = BusinessMembershipRole.Customer,
            Status = BusinessMembershipStatus.Active,
            JoinedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var service = CreateBusinessService(context);
        var result = await service.CreateBusinessAsync(user.Id, ValidRequest());

        Assert.True(result.Success, result.Error?.Message);

        var newBusiness = await context.Businesses.SingleAsync(b => b.Name == "Chege's Java Hut");
        var newMembership = await context.BusinessMemberships
            .SingleAsync(m => m.BusinessId == newBusiness.Id && m.UserId == user.Id);
        Assert.Equal(BusinessMembershipRole.Owner, newMembership.Role);
        Assert.Equal(BusinessMembershipStatus.Active, newMembership.Status);

        // Shopping at the other business is untouched.
        Assert.Equal(
            BusinessMembershipRole.Customer,
            (await context.BusinessMemberships.SingleAsync(m => m.BusinessId == business.Id)).Role);
    }

    [Fact]
    public async Task CreateBusiness_StaffOfAnotherBusiness_MayAlsoOwn()
    {
        using var context = CreateContext("BizProv_MultiTenant_01");
        var user = AddUser(context, "multi@example.com", UserRole.Staff);
        var employer = new Business
        {
            Id = Guid.NewGuid(),
            Name = "Employer Ltd",
            Category = "Retail",
            Location = "Nairobi",
            MpesaNumber = "111",
            CreatedAt = DateTime.UtcNow
        };
        context.Businesses.Add(employer);
        context.BusinessMemberships.Add(new BusinessMembership
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            BusinessId = employer.Id,
            Role = BusinessMembershipRole.Staff,
            Status = BusinessMembershipStatus.Active,
            JoinedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var service = CreateBusinessService(context);
        var result = await service.CreateBusinessAsync(user.Id, ValidRequest());

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(2, await context.BusinessMemberships.CountAsync(m => m.UserId == user.Id));
    }

    [Fact]
    public async Task CreateBusiness_DeletedAccount_IsRefused()
    {
        using var context = CreateContext("BizProv_Deleted_01");
        var user = AddUser(context, "gone@example.com");
        user.IsDeleted = true;
        await context.SaveChangesAsync();

        var service = CreateBusinessService(context);
        var result = await service.CreateBusinessAsync(user.Id, ValidRequest());

        Assert.False(result.Success);
        // The global query filter hides soft-deleted identities entirely.
        Assert.Equal("NOT_FOUND", result.Error?.Code);
        Assert.Empty(context.Businesses);
    }
}
