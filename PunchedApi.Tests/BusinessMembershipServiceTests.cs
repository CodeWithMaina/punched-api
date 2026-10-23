using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;
using PunchedApi.Infrastructure.Repositories;

namespace PunchedApi.Tests;

/// <summary>
/// Pins the invariants of <see cref="BusinessMembershipService"/> — the single writer
/// of <c>business_memberships</c> and therefore the only place tenant relationships are
/// created, upgraded or revoked.
///
/// Uses SQLite in-memory (via <see cref="BookingTestBase"/>) so the schema's own
/// guarantees are exercised too: the unique <c>(user_id, business_id)</c> index, the
/// partial “one active Owner per business” index, and the CHECK constraints.
/// </summary>
public class BusinessMembershipServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly BusinessMembershipService _service;

    public BusinessMembershipServiceTests()
    {
        _connection = BookingTestBase.CreateConnection();
        _context = BookingTestBase.CreateContext(_connection);
        _service = new BusinessMembershipService(new UnitOfWork(_context));
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private async Task<User> SeedCustomerAsync(string email)
    {
        var user = BookingTestBase.CreateCustomer(email);
        await BookingTestBase.SeedAsync(_context, user);
        return user;
    }

    private async Task<(User Owner, Business Business)> SeedOwnedBusinessAsync(string email)
    {
        var owner = BookingTestBase.CreateOwner(email);
        var business = BookingTestBase.CreateBusiness(owner.Id);
        await BookingTestBase.SeedAsync(_context, owner, business);
        return (owner, business);
    }

    // ── Write path ───────────────────────────────────────────────

    [Fact]
    public async Task EnsureAsync_NeverSavesOnItsOwn_AndCreatesOneRow()
    {
        var user = await SeedCustomerAsync("self-save@test.com");
        var (_, business) = await SeedOwnedBusinessAsync("owner-a@test.com");

        await _service.EnsureAsync(user.Id, business.Id, BusinessMembershipRole.Customer);

        // The service joins the caller's unit of work: nothing is written yet.
        Assert.Equal(0, await _context.BusinessMemberships.CountAsync(m => m.UserId == user.Id));

        await _context.SaveChangesAsync();

        var row = await _context.BusinessMemberships.SingleAsync(m => m.UserId == user.Id);
        Assert.Equal(BusinessMembershipRole.Customer, row.Role);
        Assert.Equal(BusinessMembershipStatus.Active, row.Status);
        Assert.Null(row.EndedAt);
    }

    [Fact]
    public async Task EnsureAsync_IsIdempotent_AndOnlyEverUpgradesTheRole()
    {
        var user = await SeedCustomerAsync("upgrade@test.com");
        var (_, business) = await SeedOwnedBusinessAsync("owner-b@test.com");

        await _service.EnsureAsync(user.Id, business.Id, BusinessMembershipRole.Customer);
        await _context.SaveChangesAsync();
        await _service.EnsureAsync(user.Id, business.Id, BusinessMembershipRole.Customer);
        await _context.SaveChangesAsync();

        Assert.Equal(1, await _context.BusinessMemberships.CountAsync(m => m.UserId == user.Id));

        // Customer → Staff (the invitation explicitly grants it).
        await _service.EnsureAsync(user.Id, business.Id, BusinessMembershipRole.Staff);
        await _context.SaveChangesAsync();
        Assert.Equal(
            BusinessMembershipRole.Staff,
            (await _context.BusinessMemberships.SingleAsync(m => m.UserId == user.Id)).Role);

        // A later Customer association must never demote the staff member.
        await _service.EnsureAsync(user.Id, business.Id, BusinessMembershipRole.Customer);
        await _context.SaveChangesAsync();
        Assert.Equal(
            BusinessMembershipRole.Staff,
            (await _context.BusinessMemberships.SingleAsync(m => m.UserId == user.Id)).Role);
        Assert.Equal(1, await _context.BusinessMemberships.CountAsync(m => m.UserId == user.Id));
    }

    [Fact]
    public async Task EnsureAsync_ReactivatesARevokedMembership_InPlace()
    {
        var user = await SeedCustomerAsync("rejoin@test.com");
        var (_, business) = await SeedOwnedBusinessAsync("owner-c@test.com");

        await _service.EnsureAsync(user.Id, business.Id, BusinessMembershipRole.Customer);
        await _context.SaveChangesAsync();

        var ended = await _service.EndAsync(
            user.Id, business.Id, BusinessMembershipStatus.Suspended);
        await _context.SaveChangesAsync();

        Assert.NotNull(ended);
        Assert.Equal(BusinessMembershipStatus.Suspended, ended!.Status);
        Assert.NotNull(ended.EndedAt);

        await _service.EnsureAsync(user.Id, business.Id, BusinessMembershipRole.Customer);
        await _context.SaveChangesAsync();

        var row = await _context.BusinessMemberships.SingleAsync(m => m.UserId == user.Id);
        Assert.Equal(BusinessMembershipStatus.Active, row.Status);
        Assert.Null(row.EndedAt);
    }

    [Fact]
    public async Task EnsureOwnerAsync_WritesTheOwnerRowAndTheDenormalizedPointer()
    {
        var owner = BookingTestBase.CreateOwner("owner-d@test.com");
        var business = BookingTestBase.CreateBusiness(owner.Id);
        // Model a freshly provisioned business: the membership is created by the
        // service, not handed in by the factory.
        business.Memberships.Clear();
        business.OwnerId = null;
        await BookingTestBase.SeedAsync(_context, owner, business);

        var membership = await _service.EnsureOwnerAsync(owner.Id, business);
        await _context.SaveChangesAsync();

        Assert.Equal(BusinessMembershipRole.Owner, membership.Role);
        Assert.Equal(BusinessMembershipStatus.Active, membership.Status);
        Assert.Equal(owner.Id, business.OwnerId);

        var row = await _context.BusinessMemberships.SingleAsync(m => m.BusinessId == business.Id);
        Assert.Equal(owner.Id, row.UserId);
        Assert.Equal(BusinessMembershipRole.Owner, row.Role);
    }

    [Fact]
    public async Task EndAsync_OwnerRevocationClearsThePointer_ButKeepsTheRow()
    {
        var (owner, business) = await SeedOwnedBusinessAsync("owner-e@test.com");

        var ended = await _service.EndAsync(owner.Id, business.Id);
        await _context.SaveChangesAsync();

        Assert.NotNull(ended);
        Assert.Equal(BusinessMembershipStatus.Left, ended!.Status);
        Assert.Null(await _context.Businesses
            .Where(b => b.Id == business.Id)
            .Select(b => b.OwnerId)
            .SingleAsync());

        // History is preserved: the row stays, only the status changes.
        Assert.True(await _context.BusinessMemberships.AnyAsync(m =>
            m.BusinessId == business.Id && m.UserId == owner.Id));
    }

    [Fact]
    public async Task EndAsync_RejectsActiveAsATargetStatus()
    {
        var (owner, business) = await SeedOwnedBusinessAsync("owner-f@test.com");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.EndAsync(owner.Id, business.Id, BusinessMembershipStatus.Active));
    }

    // ── Read path ────────────────────────────────────────────────

    [Fact]
    public async Task Queries_OnlySeeActiveMemberships_AndTheRightRole()
    {
        var (owner, business) = await SeedOwnedBusinessAsync("owner-g@test.com");
        var staff = BookingTestBase.CreateStaff(business.Id, "staff-g@test.com");
        var customer = BookingTestBase.CreateCustomer("customer-g@test.com");
        await BookingTestBase.SeedAsync(
            _context,
            staff,
            customer,
            BookingTestBase.CreateMembership(
                customer.Id, business.Id, BusinessMembershipRole.Customer));

        var suspended = BookingTestBase.CreateCustomer("suspended-g@test.com");
        await BookingTestBase.SeedAsync(
            _context,
            suspended,
            BookingTestBase.CreateMembership(
                suspended.Id, business.Id, BusinessMembershipRole.Staff,
                BusinessMembershipStatus.Suspended));

        Assert.True(await _service.IsActiveMemberAsync(owner.Id, business.Id));
        Assert.True(await _service.IsActiveMemberAsync(customer.Id, business.Id));
        Assert.True(await _service.IsStaffOfAsync(staff.Id, business.Id));
        Assert.False(await _service.IsStaffOfAsync(customer.Id, business.Id));
        Assert.False(await _service.IsActiveMemberAsync(suspended.Id, business.Id));
        Assert.False(await _service.HasRoleAnywhereAsync(suspended.Id, BusinessMembershipRole.Staff));

        Assert.Equal(business.Id, await _service.GetActiveBusinessIdAsync(owner.Id, BusinessMembershipRole.Owner));
        Assert.Equal(business.Id, await _service.GetActiveBusinessIdAsync(staff.Id, BusinessMembershipRole.Staff));
        Assert.Null(await _service.GetActiveBusinessIdAsync(suspended.Id, BusinessMembershipRole.Staff));

        var staffUserIds = await _service.ListActiveUserIdsAsync(business.Id, BusinessMembershipRole.Staff);
        Assert.Equal(new[] { staff.Id }, staffUserIds);

        var active = await _service.ListActiveAsync(owner.Id);
        Assert.Single(active);
        Assert.Equal(BusinessMembershipRole.Owner, active[0].Role);
    }

    [Fact]
    public async Task EnsureAsync_TwiceInTheSameUnsavedUnitOfWork_CreatesOneRow()
    {
        var user = await SeedCustomerAsync("pending@test.com");
        var (_, business) = await SeedOwnedBusinessAsync("owner-l@test.com");

        // The second call cannot see the first row through a query (it is Added,
        // not saved), so the service must answer it from the pending set.
        var first = await _service.EnsureAsync(user.Id, business.Id, BusinessMembershipRole.Customer);
        var second = await _service.EnsureAsync(user.Id, business.Id, BusinessMembershipRole.Staff);
        await _context.SaveChangesAsync();

        Assert.Same(first, second);
        Assert.Equal(BusinessMembershipRole.Staff, second.Role);
        Assert.Equal(1, await _context.BusinessMemberships.CountAsync(m => m.UserId == user.Id));
    }

    [Fact]
    public async Task FindAsync_ReturnsTheTrackedRowForThePair()
    {
        var user = await SeedCustomerAsync("find@test.com");
        var (_, business) = await SeedOwnedBusinessAsync("owner-h@test.com");

        await _service.EnsureAsync(user.Id, business.Id, BusinessMembershipRole.Customer);
        await _context.SaveChangesAsync();

        var found = await _service.FindAsync(user.Id, business.Id);
        Assert.NotNull(found);
        Assert.Equal(BusinessMembershipRole.Customer, found!.Role);
        Assert.Null(await _service.FindAsync(user.Id, Guid.NewGuid()));
    }

    // ── Schema invariants (why the service must be the only writer) ──

    [Fact]
    public async Task Schema_RejectsASecondActiveOwnerForTheSameBusiness()
    {
        var (_, business) = await SeedOwnedBusinessAsync("owner-i@test.com");
        var usurper = await SeedCustomerAsync("usurper-i@test.com");

        await _context.BusinessMemberships.AddAsync(BookingTestBase.CreateMembership(
            usurper.Id, business.Id, BusinessMembershipRole.Owner));

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => _context.SaveChangesAsync());
        Assert.Contains(
            "business_memberships.business_id",
            error.InnerException?.Message ?? error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Schema_RejectsTwoRowsForTheSameUserAndBusiness()
    {
        var user = await SeedCustomerAsync("duplicate@test.com");
        var (_, business) = await SeedOwnedBusinessAsync("owner-j@test.com");

        await _context.BusinessMemberships.AddAsync(BookingTestBase.CreateMembership(
            user.Id, business.Id, BusinessMembershipRole.Customer));
        await _context.SaveChangesAsync();

        await _context.BusinessMemberships.AddAsync(BookingTestBase.CreateMembership(
            user.Id, business.Id, BusinessMembershipRole.Staff));

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => _context.SaveChangesAsync());
        Assert.Contains(
            "business_memberships.user_id",
            error.InnerException?.Message ?? error.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Schema_RejectsAnUnknownRole()
    {
        var user = await SeedCustomerAsync("check@test.com");
        var (_, business) = await SeedOwnedBusinessAsync("owner-k@test.com");

        var insert = $"""
            INSERT INTO business_memberships
                (id, user_id, business_id, role, status, joined_at, updated_at, created_at)
            VALUES
                ('{Guid.NewGuid()}', '{user.Id}', '{business.Id}', 'Superuser', 'Active',
                 '2026-01-01 00:00:00', '2026-01-01 00:00:00', '2026-01-01 00:00:00')
            """;

        var error = await Assert.ThrowsAnyAsync<Exception>(
            () => _context.Database.ExecuteSqlRawAsync(insert));
        Assert.Contains(
            "ck_business_memberships_role",
            error.InnerException?.Message ?? error.Message,
            StringComparison.OrdinalIgnoreCase);
    }
}
