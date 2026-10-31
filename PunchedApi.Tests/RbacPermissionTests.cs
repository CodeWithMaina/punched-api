using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.Authorization;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Tests;

/// <summary>
/// Granular RBAC: deterministic default-role provisioning, effective-permission
/// resolution, cross-business isolation, privilege-escalation resistance and the
/// migration guarantee that existing memberships keep their legitimate access.
/// </summary>
public partial class RbacPermissionTests : IAsyncLifetime
{
    private Microsoft.Data.Sqlite.SqliteConnection _connection = null!;
    private ApplicationDbContext _context = null!;
    private IBusinessPermissionService _permissions = null!;

    private User _owner = null!;
    private Business _business = null!;
    private User _staff = null!;

    public async Task InitializeAsync()
    {
        _connection = BookingTestBase.CreateConnection();
        _context = BookingTestBase.CreateContext(_connection);
        _permissions = BusinessOperationsTestBase.CreatePermissionService(_context);

        _owner = BookingTestBase.CreateOwner("rbac-owner@test.com");
        _business = BookingTestBase.CreateBusiness(_owner.Id, "RBAC Salon");
        _staff = BookingTestBase.CreateStaff(_business.Id, "rbac-staff@test.com");

        await BookingTestBase.SeedAsync(_context, _owner, _business, _staff);
    }

    public Task DisposeAsync()
    {
        _context.Dispose();
        _connection.Dispose();
        return Task.CompletedTask;
    }
}

public partial class RbacPermissionTests
{
    // ===========================================================
    //  PROVISIONING
    // ===========================================================

    [Fact]
    public async Task Provisioning_creates_the_default_role_templates_once()
    {
        await _permissions.EnsureProvisionedAsync(_business.Id);
        await _permissions.EnsureProvisionedAsync(_business.Id);
        await _permissions.EnsureProvisionedAsync(_business.Id);

        var roles = await _context.BusinessRoles.AsNoTracking()
            .Where(r => r.BusinessId == _business.Id)
            .Select(r => r.Key)
            .ToListAsync();

        Assert.Equal(4, roles.Count);
        Assert.Contains("owner", roles);
        Assert.Contains("manager", roles);
        Assert.Contains("receptionist", roles);
        Assert.Contains("service_professional", roles);
    }

    [Fact]
    public async Task Exactly_one_owner_role_exists_per_business()
    {
        await _permissions.EnsureProvisionedAsync(_business.Id);

        var ownerRoles = await _context.BusinessRoles.AsNoTracking()
            .CountAsync(r => r.BusinessId == _business.Id && r.IsOwnerRole);

        Assert.Equal(1, ownerRoles);
    }

    [Fact]
    public async Task Provisioning_assigns_the_owner_role_to_the_owner_and_the_professional_role_to_staff()
    {
        await _permissions.EnsureProvisionedAsync(_business.Id);

        var ownerMembership = await _context.BusinessMemberships.AsNoTracking()
            .SingleAsync(m => m.UserId == _owner.Id && m.BusinessId == _business.Id);
        var staffMembership = await _context.BusinessMemberships.AsNoTracking()
            .SingleAsync(m => m.UserId == _staff.Id && m.BusinessId == _business.Id);

        var assignments = await _context.BusinessMembershipRoleAssignments.AsNoTracking()
            .Where(a => a.BusinessMembershipId == ownerMembership.Id || a.BusinessMembershipId == staffMembership.Id)
            .Join(_context.BusinessRoles.AsNoTracking(), a => a.BusinessRoleId, r => r.Id, (a, r) => new { a.BusinessMembershipId, r.Key })
            .ToListAsync();

        Assert.Contains(assignments, a => a.BusinessMembershipId == ownerMembership.Id && a.Key == "owner");
        Assert.Contains(assignments, a => a.BusinessMembershipId == staffMembership.Id && a.Key == "service_professional");
    }

    [Fact]
    public async Task Default_staff_role_grants_no_payout_or_role_administration()
    {
        await _permissions.EnsureProvisionedAsync(_business.Id);

        var effective = await _permissions.GetEffectivePermissionsAsync(_staff.Id, _business.Id);

        Assert.Contains("appointments.confirm", effective);
        Assert.Contains("appointments.cancel", effective);
        Assert.Contains("stamps.award", effective);
        Assert.Contains("commissions.view", effective);

        Assert.DoesNotContain("commissions.payout", effective);
        Assert.DoesNotContain("commissions.manage", effective);
        Assert.DoesNotContain("roles.manage", effective);
        Assert.DoesNotContain("staff.manage", effective);
        Assert.DoesNotContain("settings.manage", effective);
    }

    [Fact]
    public async Task The_owner_role_implicitly_holds_every_catalogue_permission()
    {
        await _permissions.EnsureProvisionedAsync(_business.Id);

        var effective = await _permissions.GetEffectivePermissionsAsync(_owner.Id, _business.Id);

        Assert.Equal(PermissionCatalog.AllCodes.Count, effective.Count);
        Assert.Contains("commissions.payout", effective);
        Assert.Contains("roles.manage", effective);
    }
}

public partial class RbacPermissionTests
{
    // ===========================================================
    //  ENFORCEMENT + ISOLATION
    // ===========================================================

    [Fact]
    public async Task A_non_member_holds_no_permission_in_the_business()
    {
        await _permissions.EnsureProvisionedAsync(_business.Id);

        var outsider = BookingTestBase.CreateCustomer("rbac-outsider@test.com");
        await BookingTestBase.SeedAsync(_context, outsider);

        Assert.False(await _permissions.HasPermissionAsync(outsider.Id, _business.Id, "appointments.view"));
        Assert.False(await _permissions.HasPermissionAsync(outsider.Id, _business.Id, "commissions.view"));
        Assert.Empty(await _permissions.GetEffectivePermissionsAsync(outsider.Id, _business.Id));
    }

    [Fact]
    public async Task A_staff_member_of_one_business_holds_nothing_in_another()
    {
        await _permissions.EnsureProvisionedAsync(_business.Id);

        var otherOwner = BookingTestBase.CreateOwner("rbac-other-owner@test.com");
        var otherBusiness = BookingTestBase.CreateBusiness(otherOwner.Id, "Other RBAC Salon");
        await BookingTestBase.SeedAsync(_context, otherOwner, otherBusiness);
        await _permissions.EnsureProvisionedAsync(otherBusiness.Id);

        Assert.False(await _permissions.HasPermissionAsync(_staff.Id, otherBusiness.Id, "appointments.view"));
        Assert.False(await _permissions.HasPermissionAsync(_owner.Id, otherBusiness.Id, "commissions.payout"));
    }

    [Fact]
    public async Task An_unknown_permission_code_is_never_granted()
    {
        await _permissions.EnsureProvisionedAsync(_business.Id);

        Assert.False(await _permissions.HasPermissionAsync(_owner.Id, _business.Id, "not.a.real.permission"));
        Assert.False(await _permissions.HasPermissionAsync(_owner.Id, _business.Id, string.Empty));
    }

    [Fact]
    public async Task A_suspended_membership_holds_nothing()
    {
        await _permissions.EnsureProvisionedAsync(_business.Id);
        Assert.True(await _permissions.HasPermissionAsync(_staff.Id, _business.Id, "appointments.view"));

        var membership = await _context.BusinessMemberships
            .SingleAsync(m => m.UserId == _staff.Id && m.BusinessId == _business.Id);
        membership.Status = BusinessMembershipStatus.Suspended;
        await _context.SaveChangesAsync();

        // Permissions are resolved per request, so the change takes effect on the
        // next check with no token refresh and no re-login.
        Assert.False(await _permissions.HasPermissionAsync(_staff.Id, _business.Id, "appointments.view"));
    }

    [Fact]
    public async Task A_role_change_takes_effect_immediately_after_invalidation()
    {
        await _permissions.EnsureProvisionedAsync(_business.Id);

        // Initially the staff member cannot pay out.
        Assert.False(await _permissions.HasPermissionAsync(_staff.Id, _business.Id, "commissions.payout"));

        var professionalRole = await _context.BusinessRoles
            .SingleAsync(r => r.BusinessId == _business.Id && r.Key == "service_professional");
        _context.BusinessRolePermissions.Add(new BusinessRolePermission
        {
            Id = Guid.NewGuid(),
            BusinessRoleId = professionalRole.Id,
            PermissionCode = "commissions.payout",
            CreatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();

        // Until the cache is dropped the memoized answer stands...
        _permissions.Invalidate(_business.Id);

        // ...and after invalidation the new grant is live on the very next check.
        Assert.True(await _permissions.HasPermissionAsync(_staff.Id, _business.Id, "commissions.payout"));
    }

    [Fact]
    public async Task A_deactivated_role_stops_granting_its_permissions()
    {
        await _permissions.EnsureProvisionedAsync(_business.Id);
        Assert.True(await _permissions.HasPermissionAsync(_staff.Id, _business.Id, "appointments.view"));

        var professionalRole = await _context.BusinessRoles
            .SingleAsync(r => r.BusinessId == _business.Id && r.Key == "service_professional");
        professionalRole.IsActive = false;
        await _context.SaveChangesAsync();
        _permissions.Invalidate(_business.Id);

        Assert.False(await _permissions.HasPermissionAsync(_staff.Id, _business.Id, "appointments.view"));
    }

    [Fact]
    public async Task Existing_staff_memberships_keep_their_operational_access_and_gain_no_admin_authority()
    {
        // The migration guarantee: a staff membership that existed before RBAC
        // must keep working, and must not silently acquire administrative power.
        await _permissions.EnsureProvisionedAsync(_business.Id);
        var effective = await _permissions.GetEffectivePermissionsAsync(_staff.Id, _business.Id);

        foreach (var code in new[]
                 {
                     "appointments.view", "appointments.confirm", "appointments.complete",
                     "appointments.no_show", "appointments.cancel", "appointments.reschedule",
                     "customers.view", "staff.view", "stamps.award", "stamps.view",
                     "attendance.clock", "attendance.view", "loyalty.stamp", "loyalty.redeem",
                     "redemptions.fulfill", "reviews.view", "notifications.view"
                 })
        {
            Assert.Contains(code, effective);
        }

        foreach (var forbidden in new[]
                 {
                     "commissions.payout", "commissions.manage", "commissions.approve",
                     "roles.manage", "staff.manage", "settings.manage",
                     "payments.configure", "payments.reverse", "loyalty.manage",
                     "analytics.view", "customers.delete", "customers.manage"
                 })
        {
            Assert.DoesNotContain(forbidden, effective);
        }
    }}
