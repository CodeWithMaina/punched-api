using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.Authorization;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Tests;

/// <summary>
/// RBAC management: the permission catalogue, custom role CRUD, permission
/// grants, member assignment, and the security rules the whole model depends on
/// (owner-role immutability, no privilege escalation, no cross-business
/// assignment, unknown permissions fail closed, deactivated roles stop granting).
/// </summary>
public partial class BusinessRoleManagementTests : IAsyncLifetime
{
    private Microsoft.Data.Sqlite.SqliteConnection _connection = null!;
    private ApplicationDbContext _context = null!;
    private BusinessRoleService _roles = null!;
    private IBusinessPermissionService _permissions = null!;

    private User _owner = null!;
    private Business _business = null!;
    private User _manager = null!;
    private User _staff = null!;
    private User _otherOwner = null!;
    private Business _otherBusiness = null!;

    public async Task InitializeAsync()
    {
        _connection = BookingTestBase.CreateConnection();
        _context = BookingTestBase.CreateContext(_connection);

        _permissions = BusinessOperationsTestBase.CreatePermissionService(_context);
        _roles = new BusinessRoleService(
            _context,
            BusinessOperationsTestBase.CreateScopeResolver(_context),
            _permissions,
            TestHelpers.CreateLogger<BusinessRoleService>());

        _owner = BookingTestBase.CreateOwner("rbacm-owner@test.com");
        _business = BookingTestBase.CreateBusiness(_owner.Id, "RBAC Management Salon");
        _staff = BookingTestBase.CreateStaff(_business.Id, "rbacm-staff@test.com");
        _manager = BookingTestBase.CreateStaff(_business.Id, "rbacm-manager@test.com");

        _otherOwner = BookingTestBase.CreateOwner("rbacm-other@test.com");
        _otherBusiness = BookingTestBase.CreateBusiness(_otherOwner.Id, "Other Salon");

        await BookingTestBase.SeedAsync(_context, _owner, _business, _staff, _manager, _otherOwner, _otherBusiness);

        await BusinessOperationsTestBase.ProvisionAsync(_context, _business.Id);
        await BusinessOperationsTestBase.ProvisionAsync(_context, _otherBusiness.Id);
    }

    public Task DisposeAsync()
    {
        _context.Dispose();
        _connection.Dispose();
        return Task.CompletedTask;
    }

    private Task<Guid> RoleIdAsync(string key) =>
        _context.BusinessRoles.AsNoTracking()
            .Where(r => r.BusinessId == _business.Id && r.Key == key)
            .Select(r => r.Id)
            .SingleAsync();

    /// <summary>
    /// Gives the manager authority to administer roles WITHOUT full authority.
    ///
    /// <para>The seeded Manager template deliberately excludes <c>roles.manage</c>
    /// ("cannot administer roles"), so it cannot model the escalation case. This
    /// builds a custom "Role Admin" role that holds <c>roles.view</c>,
    /// <c>roles.manage</c> and a couple of operational permissions — enough to
    /// reach the role editor, but still lacking payouts, settings and approvals.
    /// That is exactly the caller an escalation guard has to stop.</para>
    /// </summary>
    private async Task PromoteManagerAsync()
    {
        var roleAdmin = await _roles.CreateRoleAsync(_owner.Id, "Business", new CreateBusinessRoleRequest
        {
            Name = "Role Admin",
            Permissions = new[] { "roles.view", "roles.manage", "appointments.view", "appointments.confirm" }
        });

        await _roles.AssignRoleAsync(_owner.Id, "Business", _manager.Id,
            new AssignMembershipRoleRequest { RoleId = roleAdmin.Data!.Id });
    }
}

public partial class BusinessRoleManagementTests
{
    // ===========================================================
    //  PERMISSION CATALOGUE
    // ===========================================================

    [Fact]
    public async Task The_permission_catalogue_is_exposed_grouped_by_module()
    {
        var catalogue = await _roles.GetPermissionCatalogueAsync(_owner.Id, "Business");

        Assert.True(catalogue.Success);
        Assert.Equal(PermissionCatalog.AllCodes.Count, catalogue.Data!.Permissions.Count);
        Assert.NotEmpty(catalogue.Data.ByModule);

        // Every advertised code must exist in the code-owned catalogue, so the
        // API can never offer a permission with no backend operation behind it.
        Assert.All(catalogue.Data.Permissions, p => Assert.True(PermissionCatalog.Exists(p.Code)));
    }

    [Theory]
    [InlineData("appointments.confirm")]
    [InlineData("appointments.approve_reschedule")]
    [InlineData("customers.manage")]
    [InlineData("staff.manage")]
    [InlineData("staff.positions.manage")]
    [InlineData("serviceCatalog.manage")]
    [InlineData("serviceCategories.manage")]
    [InlineData("commissions.approve")]
    [InlineData("payments.confirm_cash")]
    [InlineData("analytics.view")]
    [InlineData("settings.manage")]
    [InlineData("roles.view")]
    [InlineData("roles.manage")]
    public void The_catalogue_covers_every_implemented_operation(string code)
    {
        Assert.True(PermissionCatalog.Exists(code), $"Missing catalogue permission: {code}");
    }

    [Fact]
    public async Task The_default_role_templates_are_provisioned_idempotently()
    {
        await _permissions.EnsureProvisionedAsync(_business.Id);
        await _permissions.EnsureProvisionedAsync(_business.Id);

        var roles = await _roles.GetRolesAsync(_owner.Id, "Business");

        Assert.Equal(4, roles.Data!.Count);
        Assert.Contains(roles.Data, r => r.Key == "owner" && r.IsOwnerRole);
        Assert.Contains(roles.Data, r => r.Key == "manager" && !r.IsOwnerRole);
        Assert.Contains(roles.Data, r => r.Key == "receptionist" && !r.IsOwnerRole);
        Assert.Contains(roles.Data, r => r.Key == "service_professional" && !r.IsOwnerRole);
    }

    // ===========================================================
    //  CUSTOM ROLE CRUD
    // ===========================================================

    [Fact]
    public async Task An_owner_can_create_a_custom_role_with_permissions()
    {
        var created = await _roles.CreateRoleAsync(_owner.Id, "Business", new CreateBusinessRoleRequest
        {
            Name = "Front Desk Lead",
            Description = "Runs the desk.",
            Permissions = new[] { "appointments.view", "appointments.confirm", "customers.view" }
        });

        Assert.True(created.Success);
        Assert.Equal("Front Desk Lead", created.Data!.Name);
        Assert.False(created.Data.IsOwnerRole);
        Assert.False(created.Data.IsSystem);
        Assert.Equal(3, created.Data.Permissions.Count);
        Assert.Contains("appointments.confirm", created.Data.Permissions);
    }

    [Fact]
    public async Task An_unknown_permission_code_fails_closed()
    {
        var created = await _roles.CreateRoleAsync(_owner.Id, "Business", new CreateBusinessRoleRequest
        {
            Name = "Bogus",
            Permissions = new[] { "appointments.view", "not.a.permission" }
        });

        Assert.Equal("UNKNOWN_PERMISSION", created.Error?.Code);
        Assert.False(await _context.BusinessRoles.AnyAsync(r => r.Name == "Bogus"));
    }

    [Fact]
    public async Task Duplicate_role_names_and_keys_are_rejected()
    {
        await _roles.CreateRoleAsync(_owner.Id, "Business", new CreateBusinessRoleRequest { Name = "Shift Lead" });

        Assert.Equal("ROLE_NAME_EXISTS", (await _roles.CreateRoleAsync(_owner.Id, "Business",
            new CreateBusinessRoleRequest { Name = "shift lead" })).Error?.Code);

        Assert.Equal("ROLE_KEY_EXISTS", (await _roles.CreateRoleAsync(_owner.Id, "Business",
            new CreateBusinessRoleRequest { Name = "Another Name", Key = "shift_lead" })).Error?.Code);
    }

    [Fact]
    public async Task Granting_and_revoking_permissions_is_idempotent()
    {
        var role = await _roles.CreateRoleAsync(_owner.Id, "Business",
            new CreateBusinessRoleRequest { Name = "Shift Lead" });

        await _roles.AddRolePermissionAsync(_owner.Id, "Business", role.Data!.Id,
            new RolePermissionChangeRequest { PermissionCode = "appointments.view" });
        var granted = await _roles.AddRolePermissionAsync(_owner.Id, "Business", role.Data.Id,
            new RolePermissionChangeRequest { PermissionCode = "appointments.view" });
        Assert.Single(granted.Data!.Permissions);

        await _roles.RemoveRolePermissionAsync(_owner.Id, "Business", role.Data.Id, "appointments.view");
        var revoked = await _roles.RemoveRolePermissionAsync(_owner.Id, "Business", role.Data.Id, "appointments.view");
        Assert.Empty(revoked.Data!.Permissions);
    }

    [Fact]
    public async Task Replacing_a_permission_set_applies_the_exact_requested_set()
    {
        var role = await _roles.CreateRoleAsync(_owner.Id, "Business", new CreateBusinessRoleRequest
        {
            Name = "Shift Lead",
            Permissions = new[] { "appointments.view", "customers.view" }
        });

        var replaced = await _roles.SetRolePermissionsAsync(_owner.Id, "Business", role.Data!.Id,
            new SetRolePermissionsRequest { Permissions = new[] { "appointments.view", "appointments.confirm" } });

        Assert.Equal(new[] { "appointments.confirm", "appointments.view" }, replaced.Data!.Permissions);
    }
}

public partial class BusinessRoleManagementTests
{
    // ===========================================================
    //  MEMBER ASSIGNMENT
    // ===========================================================

    [Fact]
    public async Task A_role_can_be_assigned_to_a_member_and_shows_the_effective_union()
    {
        var role = await _roles.CreateRoleAsync(_owner.Id, "Business", new CreateBusinessRoleRequest
        {
            Name = "Shift Lead",
            Permissions = new[] { "appointments.view", "appointments.confirm" }
        });

        var assigned = await _roles.AssignRoleAsync(_owner.Id, "Business", _staff.Id,
            new AssignMembershipRoleRequest { RoleId = role.Data!.Id });

        Assert.True(assigned.Success);
        Assert.Contains(assigned.Data!.Roles, r => r.RoleId == role.Data.Id);
        Assert.Contains("appointments.confirm", assigned.Data.EffectivePermissions);
        Assert.Contains("appointments.view", assigned.Data.EffectivePermissions);
    }

    [Fact]
    public async Task Assigning_a_role_twice_is_idempotent()
    {
        var role = await _roles.CreateRoleAsync(_owner.Id, "Business",
            new CreateBusinessRoleRequest { Name = "Shift Lead", Permissions = new[] { "appointments.view" } });

        await _roles.AssignRoleAsync(_owner.Id, "Business", _staff.Id, new AssignMembershipRoleRequest { RoleId = role.Data!.Id });
        var again = await _roles.AssignRoleAsync(_owner.Id, "Business", _staff.Id, new AssignMembershipRoleRequest { RoleId = role.Data.Id });

        Assert.Single(again.Data!.Roles.Where(r => r.RoleId == role.Data.Id));
        Assert.Equal(1, await _context.BusinessMembershipRoleAssignments.CountAsync(a => a.BusinessRoleId == role.Data.Id));
    }

    [Fact]
    public async Task Removing_a_role_revokes_its_permissions_immediately()
    {
        var role = await _roles.CreateRoleAsync(_owner.Id, "Business",
            new CreateBusinessRoleRequest { Name = "Shift Lead", Permissions = new[] { "customers.manage" } });
        await _roles.AssignRoleAsync(_owner.Id, "Business", _staff.Id, new AssignMembershipRoleRequest { RoleId = role.Data!.Id });

        Assert.True(await _permissions.HasPermissionAsync(_staff.Id, _business.Id, "customers.manage"));

        var removed = await _roles.RemoveRoleAsync(_owner.Id, "Business", _staff.Id, role.Data.Id);

        Assert.DoesNotContain(removed.Data!.Roles, r => r.RoleId == role.Data.Id);
        Assert.False(await _permissions.HasPermissionAsync(_staff.Id, _business.Id, "customers.manage"));
    }

    [Fact]
    public async Task Deactivating_a_role_stops_it_granting_without_losing_its_grants()
    {
        var role = await _roles.CreateRoleAsync(_owner.Id, "Business",
            new CreateBusinessRoleRequest { Name = "Shift Lead", Permissions = new[] { "customers.manage" } });
        await _roles.AssignRoleAsync(_owner.Id, "Business", _staff.Id, new AssignMembershipRoleRequest { RoleId = role.Data!.Id });

        await _roles.UpdateRoleAsync(_owner.Id, "Business", role.Data.Id, new UpdateBusinessRoleRequest { IsActive = false });

        Assert.False(await _permissions.HasPermissionAsync(_staff.Id, _business.Id, "customers.manage"));

        var reloaded = await _roles.GetRoleAsync(_owner.Id, "Business", role.Data.Id);
        Assert.Single(reloaded.Data!.Permissions);
        Assert.Equal(1, reloaded.Data.MemberCount);

        // Reactivating restores exactly the prior authority.
        await _roles.UpdateRoleAsync(_owner.Id, "Business", role.Data.Id, new UpdateBusinessRoleRequest { IsActive = true });
        Assert.True(await _permissions.HasPermissionAsync(_staff.Id, _business.Id, "customers.manage"));
    }

    [Fact]
    public async Task A_suspended_membership_loses_business_access()
    {
        var role = await _roles.CreateRoleAsync(_owner.Id, "Business",
            new CreateBusinessRoleRequest { Name = "Shift Lead", Permissions = new[] { "customers.manage" } });
        await _roles.AssignRoleAsync(_owner.Id, "Business", _staff.Id, new AssignMembershipRoleRequest { RoleId = role.Data!.Id });

        var membership = await _context.BusinessMemberships.SingleAsync(m => m.BusinessId == _business.Id && m.UserId == _staff.Id);
        membership.Status = BusinessMembershipStatus.Suspended;
        await _context.SaveChangesAsync();
        _permissions.Invalidate(_business.Id);

        Assert.False(await _permissions.HasPermissionAsync(_staff.Id, _business.Id, "customers.manage"));
    }
}

public partial class BusinessRoleManagementTests
{
    // ===========================================================
    //  OWNER PROTECTION
    // ===========================================================

    [Fact]
    public async Task The_owner_role_cannot_be_edited_deactivated_or_deleted()
    {
        var ownerRoleId = await RoleIdAsync("owner");

        Assert.Equal("OWNER_ROLE_IMMUTABLE", (await _roles.UpdateRoleAsync(_owner.Id, "Business", ownerRoleId,
            new UpdateBusinessRoleRequest { Name = "Weakened" })).Error?.Code);
        Assert.Equal("OWNER_ROLE_IMMUTABLE", (await _roles.UpdateRoleAsync(_owner.Id, "Business", ownerRoleId,
            new UpdateBusinessRoleRequest { IsActive = false })).Error?.Code);
        Assert.Equal("OWNER_ROLE_IMMUTABLE", (await _roles.DeleteRoleAsync(_owner.Id, "Business", ownerRoleId)).Error?.Code);
        Assert.Equal("OWNER_ROLE_IMMUTABLE", (await _roles.SetRolePermissionsAsync(_owner.Id, "Business", ownerRoleId,
            new SetRolePermissionsRequest { Permissions = Array.Empty<string>() })).Error?.Code);

        var reloaded = await _roles.GetRoleAsync(_owner.Id, "Business", ownerRoleId);
        Assert.Equal("owner", reloaded.Data!.Key);
        Assert.True(reloaded.Data.IsActive);
    }

    [Fact]
    public async Task The_owner_role_cannot_be_assigned_to_a_non_owner_membership()
    {
        var ownerRoleId = await RoleIdAsync("owner");

        var refused = await _roles.AssignRoleAsync(_owner.Id, "Business", _staff.Id,
            new AssignMembershipRoleRequest { RoleId = ownerRoleId });

        Assert.Equal("FORBIDDEN", refused.Error?.Code);
        Assert.False(await _context.BusinessMembershipRoleAssignments.AnyAsync(a =>
            a.BusinessRoleId == ownerRoleId &&
            a.BusinessMembershipId == _context.BusinessMemberships
                .Where(m => m.BusinessId == _business.Id && m.UserId == _staff.Id).Select(m => m.Id).First()));
    }

    [Fact]
    public async Task The_owner_role_cannot_be_removed_from_the_owner()
    {
        var ownerRoleId = await RoleIdAsync("owner");

        var refused = await _roles.RemoveRoleAsync(_owner.Id, "Business", _owner.Id, ownerRoleId);

        Assert.Equal("OWNER_ROLE_IMMUTABLE", refused.Error?.Code);
        Assert.True(await _permissions.HasPermissionAsync(_owner.Id, _business.Id, "roles.manage"));
    }

    [Fact]
    public async Task A_default_template_role_cannot_be_deleted_but_can_be_deactivated()
    {
        var managerRoleId = await RoleIdAsync("manager");

        Assert.Equal("ROLE_SYSTEM_PROTECTED", (await _roles.DeleteRoleAsync(_owner.Id, "Business", managerRoleId)).Error?.Code);
        Assert.True((await _roles.UpdateRoleAsync(_owner.Id, "Business", managerRoleId,
            new UpdateBusinessRoleRequest { IsActive = false })).Success);
    }

    [Fact]
    public async Task A_custom_role_someone_holds_cannot_be_deleted()
    {
        var role = await _roles.CreateRoleAsync(_owner.Id, "Business", new CreateBusinessRoleRequest { Name = "Shift Lead" });
        await _roles.AssignRoleAsync(_owner.Id, "Business", _staff.Id, new AssignMembershipRoleRequest { RoleId = role.Data!.Id });

        Assert.Equal("ROLE_IN_USE", (await _roles.DeleteRoleAsync(_owner.Id, "Business", role.Data.Id)).Error?.Code);

        await _roles.RemoveRoleAsync(_owner.Id, "Business", _staff.Id, role.Data.Id);
        Assert.True((await _roles.DeleteRoleAsync(_owner.Id, "Business", role.Data.Id)).Success);
    }
}

public partial class BusinessRoleManagementTests
{
    // ===========================================================
    //  PRIVILEGE ESCALATION
    // ===========================================================

    [Fact]
    public async Task A_manager_cannot_grant_a_permission_it_does_not_hold()
    {
        await PromoteManagerAsync();

        // Sanity: the seeded Manager template deliberately lacks payouts.
        Assert.False(await _permissions.HasPermissionAsync(_manager.Id, _business.Id, "commissions.payout"));
        Assert.True(await _permissions.HasPermissionAsync(_manager.Id, _business.Id, "roles.manage"));

        var refused = await _roles.CreateRoleAsync(_manager.Id, "Business", new CreateBusinessRoleRequest
        {
            Name = "Payout Runner",
            Permissions = new[] { "commissions.payout" }
        });

        Assert.Equal("PERMISSION_ESCALATION", refused.Error?.Code);
        Assert.False(await _context.BusinessRoles.AnyAsync(r => r.Name == "Payout Runner"));
    }

    [Fact]
    public async Task A_manager_can_grant_a_permission_it_does_hold()
    {
        await PromoteManagerAsync();

        var allowed = await _roles.CreateRoleAsync(_manager.Id, "Business", new CreateBusinessRoleRequest
        {
            Name = "Front Desk Lead",
            Permissions = new[] { "appointments.confirm" }
        });

        Assert.True(allowed.Success);
        Assert.Contains("appointments.confirm", allowed.Data!.Permissions);
    }

    [Fact]
    public async Task Adding_a_permission_the_caller_lacks_is_refused()
    {
        await PromoteManagerAsync();
        var role = await _roles.CreateRoleAsync(_manager.Id, "Business",
            new CreateBusinessRoleRequest { Name = "Front Desk Lead", Permissions = new[] { "appointments.view" } });

        // A code the caller itself holds may be passed on.
        var allowed = await _roles.AddRolePermissionAsync(_manager.Id, "Business", role.Data!.Id,
            new RolePermissionChangeRequest { PermissionCode = "roles.manage" });
        Assert.True(allowed.Success);
        Assert.Contains("roles.manage", allowed.Data!.Permissions);

        // A code the caller does NOT hold may not.
        var escalate = await _roles.AddRolePermissionAsync(_manager.Id, "Business", role.Data.Id,
            new RolePermissionChangeRequest { PermissionCode = "settings.manage" });
        Assert.Equal("PERMISSION_ESCALATION", escalate.Error?.Code);
        Assert.DoesNotContain("settings.manage", (await _roles.GetRoleAsync(_owner.Id, "Business", role.Data.Id)).Data!.Permissions);
    }

    [Fact]
    public async Task A_manager_cannot_assign_a_role_that_grants_authority_it_lacks()
    {
        await PromoteManagerAsync();

        // The owner builds a powerful role the manager does not hold.
        var powerful = await _roles.CreateRoleAsync(_owner.Id, "Business", new CreateBusinessRoleRequest
        {
            Name = "Payout Runner",
            Permissions = new[] { "commissions.payout" }
        });

        var refused = await _roles.AssignRoleAsync(_manager.Id, "Business", _staff.Id,
            new AssignMembershipRoleRequest { RoleId = powerful.Data!.Id });

        Assert.Equal("PERMISSION_ESCALATION", refused.Error?.Code);
    }

    [Fact]
    public async Task A_staff_member_without_roles_manage_cannot_administer_roles_at_all()
    {
        var role = await _roles.CreateRoleAsync(_owner.Id, "Business",
            new CreateBusinessRoleRequest { Name = "Shift Lead", Permissions = new[] { "appointments.view" } });

        Assert.Equal("FORBIDDEN", (await _roles.CreateRoleAsync(_staff.Id, "Business",
            new CreateBusinessRoleRequest { Name = "Rogue" })).Error?.Code);
        Assert.Equal("FORBIDDEN", (await _roles.UpdateRoleAsync(_staff.Id, "Business", role.Data!.Id,
            new UpdateBusinessRoleRequest { Name = "Renamed" })).Error?.Code);
        Assert.Equal("FORBIDDEN", (await _roles.SetRolePermissionsAsync(_staff.Id, "Business", role.Data.Id,
            new SetRolePermissionsRequest { Permissions = new[] { "roles.manage" } })).Error?.Code);
        Assert.Equal("FORBIDDEN", (await _roles.AssignRoleAsync(_staff.Id, "Business", _staff.Id,
            new AssignMembershipRoleRequest { RoleId = role.Data.Id })).Error?.Code);
    }

    // ===========================================================
    //  CROSS-BUSINESS ISOLATION
    // ===========================================================

    [Fact]
    public async Task Another_business_role_is_indistinguishable_from_a_missing_one()
    {
        var foreign = await _roles.CreateRoleAsync(_otherOwner.Id, "Business",
            new CreateBusinessRoleRequest { Name = "Their Role" });

        Assert.Equal("NOT_FOUND", (await _roles.GetRoleAsync(_owner.Id, "Business", foreign.Data!.Id)).Error?.Code);
        Assert.Equal("NOT_FOUND", (await _roles.UpdateRoleAsync(_owner.Id, "Business", foreign.Data.Id,
            new UpdateBusinessRoleRequest { Name = "Stolen" })).Error?.Code);
        Assert.Equal("NOT_FOUND", (await _roles.DeleteRoleAsync(_owner.Id, "Business", foreign.Data.Id)).Error?.Code);
        Assert.Equal("NOT_FOUND", (await _roles.AssignRoleAsync(_owner.Id, "Business", _staff.Id,
            new AssignMembershipRoleRequest { RoleId = foreign.Data.Id })).Error?.Code);
    }

    [Fact]
    public async Task A_role_from_another_business_cannot_be_assigned_to_this_business_member()
    {
        var foreign = await _roles.CreateRoleAsync(_otherOwner.Id, "Business",
            new CreateBusinessRoleRequest { Name = "Their Role", Permissions = new[] { "appointments.view" } });

        var refused = await _roles.AssignRoleAsync(_owner.Id, "Business", _staff.Id,
            new AssignMembershipRoleRequest { RoleId = foreign.Data!.Id });

        Assert.Equal("NOT_FOUND", refused.Error?.Code);
        Assert.Empty(await _context.BusinessMembershipRoleAssignments
            .Where(a => a.BusinessRoleId == foreign.Data.Id).ToListAsync());
    }
}
