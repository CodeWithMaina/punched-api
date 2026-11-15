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
/// "Recently added" on the staff roster.
///
/// <para><b>What was wrong.</b> The sort was <c>OrderByDescending(s =&gt; s.UserId)</c>. A user
/// id is a random GUID, so the list came back in an order that did not correspond to any date:
/// a member added this morning could appear below one added last year. Nothing failed, because
/// the column was never checked against reality.</para>
///
/// <para>These tests pin the sort to the membership's own <c>joined_at</c>, prove a re-added
/// member moves to the top (which a created-at sort could not do), and prove the order is
/// deterministic so paging cannot repeat or skip a member.</para>
/// </summary>
public class StaffAddedSortTests
{
    private sealed class Env : IDisposable
    {
        public Microsoft.Data.Sqlite.SqliteConnection Connection = null!;
        public ApplicationDbContext Context = null!;
        public BusinessService Service = null!;
        public Business Business = null!;
        public User Owner = null!;

        public void Dispose()
        {
            Context.Dispose();
            Connection.Dispose();
        }
    }

    private static User Staff(string name, DateTime accountCreatedAt)
    {
        var user = BookingTestBase.CreateStaffWithoutMembership($"{name.Replace(" ", ".").ToLowerInvariant()}@test.com");
        user.FullName = name;
        user.CreatedAt = accountCreatedAt;
        return user;
    }

    /// <summary>A membership whose "joined" time is explicit, so the fixture cannot drift.</summary>
    private static BusinessMembership Membership(
        Guid userId, Guid businessId, DateTime joinedAt, BusinessMembershipRole role = BusinessMembershipRole.Staff) =>
        new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            BusinessId = businessId,
            Role = role,
            Status = BusinessMembershipStatus.Active,
            JoinedAt = joinedAt,
            CreatedAt = joinedAt,
            UpdatedAt = joinedAt,
        };

    private static async Task<Env> CreateEnvAsync(Action<ApplicationDbContext, Business, Env> seed)
    {
        var connection = BookingTestBase.CreateConnection();
        var context = BookingTestBase.CreateContext(connection);

        var owner = BookingTestBase.CreateOwner("sort-owner@test.com");
        // CreateBusiness attaches the canonical Owner membership to its navigation, which is
        // what authorizes this owner in GetMyStaffAsync — so it must not be added again.
        var business = BookingTestBase.CreateBusiness(owner.Id, "Sort Studio");

        context.Users.Add(owner);
        context.Businesses.Add(business);
        await context.SaveChangesAsync();

        var env = new Env
        {
            Connection = connection,
            Context = context,
            Business = business,
            Owner = owner,
            Service = new BusinessService(
                new UnitOfWork(context),
                context,
                new Mock<IInsightService>().Object,
                new Mock<IBusinessScopeResolver>().Object,
                new Mock<ISubscriptionProvisioningService>().Object,
                new Mock<IModuleEntitlementService>().Object,
                new Mock<IBusinessSlugGenerator>().Object,
                new Mock<IBusinessMembershipResolver>().Object,
                TestHelpers.CreateLogger<BusinessService>()),
        };

        seed(context, business, env);
        await context.SaveChangesAsync();
        return env;
    }

    private static Task<PunchedApi.Application.DTOs.ApiResponse<PunchedApi.Application.DTOs.StaffListResponse>> RosterAsync(
        Env env, string sortBy = "added", string sortDirection = "desc", int page = 1, int pageSize = 20) =>
        env.Service.GetMyStaffAsync(env.Owner.Id, sortBy: sortBy, sortDirection: sortDirection, page: page, pageSize: pageSize);

    // ── The sort ────────────────────────────────────────────

    [Fact]
    public async Task RecentlyAdded_OrdersByWhenEachMemberJoined_NotByTheirUserId()
    {
        var now = DateTime.UtcNow;

        // Three members. Their ids are random GUIDs, but their join order is fixed: Ada
        // first, Bo second, Cyd last — and their ACCOUNT creation dates run the other way,
        // so an order that followed the user record would be visibly wrong.
        var ada = Staff("Ada First", now.AddYears(-2));
        var bo = Staff("Bo Second", now.AddYears(-1));
        var cyd = Staff("Cyd Third", now);

        using var env = await CreateEnvAsync((context, business, _) =>
        {
            context.Users.AddRange(ada, bo, cyd);
            context.BusinessMemberships.AddRange(
                Membership(ada.Id, business.Id, now.AddDays(-30)),
                Membership(bo.Id, business.Id, now.AddDays(-10)),
                Membership(cyd.Id, business.Id, now.AddDays(-1)));
        });

        var newestFirst = await RosterAsync(env, "added", "desc");
        Assert.True(newestFirst.Success, newestFirst.Error?.Message);
        Assert.Equal(["Cyd Third", "Bo Second", "Ada First"], newestFirst.Data!.Items.Select(i => i.FullName));

        var oldestFirst = await RosterAsync(env, "added", "asc");
        Assert.Equal(["Ada First", "Bo Second", "Cyd Third"], oldestFirst.Data!.Items.Select(i => i.FullName));
    }

    [Fact]
    public async Task RecentlyAdded_MovesAMemberWhoWasReAddedBackToTheTop()
    {
        var now = DateTime.UtcNow;
        var old = Staff("Ada Longstanding", now.AddYears(-3));
        var returned = Staff("Bea Returned", now.AddYears(-3));

        using var env = await CreateEnvAsync((context, business, _) =>
        {
            context.Users.AddRange(old, returned);
            context.BusinessMemberships.AddRange(
                Membership(old.Id, business.Id, now.AddYears(-1)),
                // Left the team, then came back yesterday. Joined-at is refreshed when a
                // member is re-added, which is what "recently added" has to mean.
                Membership(returned.Id, business.Id, now.AddDays(-1)));
        });

        var roster = await RosterAsync(env, "added", "desc");

        Assert.Equal(["Bea Returned", "Ada Longstanding"], roster.Data!.Items.Select(i => i.FullName));
    }

    [Fact]
    public async Task RecentlyAdded_IsDeterministicWhenTwoMembersJoinedAtTheSameMoment()
    {
        var joined = DateTime.UtcNow.AddDays(-2);
        var zoe = Staff("Zoe Zebra", joined);
        var amy = Staff("Amy Aardvark", joined);

        using var env = await CreateEnvAsync((context, business, _) =>
        {
            context.Users.AddRange(zoe, amy);
            context.BusinessMemberships.AddRange(
                Membership(zoe.Id, business.Id, joined),
                Membership(amy.Id, business.Id, joined));
        });

        // Same instant, so the tie must be broken by name — the same way on every request,
        // which is what stops a page boundary from repeating one member and skipping another.
        var first = await RosterAsync(env, "added", "desc");
        var second = await RosterAsync(env, "added", "desc");
        Assert.Equal(["Amy Aardvark", "Zoe Zebra"], first.Data!.Items.Select(i => i.FullName));
        Assert.Equal(first.Data.Items.Select(i => i.UserId), second.Data!.Items.Select(i => i.UserId));
    }

    [Fact]
    public async Task RecentlyAdded_PagesThroughEveryMemberExactlyOnce()
    {
        var now = DateTime.UtcNow;
        var members = Enumerable.Range(0, 5)
            .Select(i => Staff($"Member {i}", now.AddDays(-i)))
            .ToArray();

        using var env = await CreateEnvAsync((context, business, _) =>
        {
            context.Users.AddRange(members);
            foreach (var (member, index) in members.Select((m, i) => (m, i)))
                context.BusinessMemberships.Add(Membership(member.Id, business.Id, now.AddMinutes(-index)));
        });

        var pageOne = await RosterAsync(env, "added", "desc", page: 1, pageSize: 2);
        var pageTwo = await RosterAsync(env, "added", "desc", page: 2, pageSize: 2);
        var pageThree = await RosterAsync(env, "added", "desc", page: 3, pageSize: 2);

        var ids = pageOne.Data!.Items
            .Concat(pageTwo.Data!.Items)
            .Concat(pageThree.Data!.Items)
            .Select(i => i.UserId)
            .ToList();

        Assert.Equal(5, ids.Count);
        Assert.Equal(5, ids.Distinct().Count());
        // Page 1 is the most recently added, and the last page is the oldest.
        Assert.Equal(members[0].Id, pageOne.Data.Items[0].UserId);
        Assert.Equal(members[4].Id, pageThree.Data.Items[0].UserId);
    }

    [Fact]
    public async Task RecentlyAdded_IgnoresMembershipsAtOtherBusinesses()
    {
        var now = DateTime.UtcNow;
        var mine = Staff("Mine Member", now.AddDays(-5));
        var theirs = Staff("Theirs Member", now.AddDays(-5));

        using var env = await CreateEnvAsync((context, business, _) =>
        {
            var otherOwner = BookingTestBase.CreateOwner("other-sort-owner@test.com");
            // CreateBusiness attaches that owner's membership; it must not be added again.
            var otherBusiness = BookingTestBase.CreateBusiness(otherOwner.Id, "Other Sort Studio");
            context.Users.AddRange(otherOwner, mine, theirs);
            context.Businesses.Add(otherBusiness);
            context.BusinessMemberships.AddRange(
                Membership(mine.Id, business.Id, now.AddDays(-3)),
                // Joined THIS business long ago, and the OTHER business yesterday: the read
                // must be scoped to this business, or the other membership would sort them top.
                Membership(theirs.Id, business.Id, now.AddDays(-100)),
                Membership(theirs.Id, otherBusiness.Id, now.AddDays(-1)));
        });

        var roster = await RosterAsync(env, "added", "desc");

        Assert.Equal(["Mine Member", "Theirs Member"], roster.Data!.Items.Select(i => i.FullName));
    }

    [Fact]
    public async Task TheRosterReportsWhenEachMemberJoined()
    {
        var now = DateTime.UtcNow;
        var joined = now.AddDays(-4);
        var member = Staff("Joined Member", now.AddYears(-2));

        using var env = await CreateEnvAsync((context, business, _) =>
        {
            context.Users.Add(member);
            context.BusinessMemberships.Add(Membership(member.Id, business.Id, joined));
        });

        var roster = await RosterAsync(env, "added", "desc");

        var row = Assert.Single(roster.Data!.Items);
        // The membership's joined-at, not the account's creation date.
        Assert.Equal(joined, row.JoinedAt, TimeSpan.FromSeconds(1));
    }
}
