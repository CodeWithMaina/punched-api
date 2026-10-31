using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Tests;

/// <summary>
/// Staff position (job title) management: CRUD, deterministic ordering,
/// assignment and reassignment with an auditable history, deactivation, deletion
/// protection, cross-business isolation, and the guarantee that a title never
/// grants a permission.
/// </summary>
public partial class StaffPositionTests : IAsyncLifetime
{
    private Microsoft.Data.Sqlite.SqliteConnection _connection = null!;
    private ApplicationDbContext _context = null!;
    private StaffPositionService _positions = null!;

    private User _owner = null!;
    private Business _business = null!;
    private User _staff = null!;
    private User _otherOwner = null!;
    private Business _otherBusiness = null!;
    private User _otherStaff = null!;

    public async Task InitializeAsync()
    {
        _connection = BookingTestBase.CreateConnection();
        _context = BookingTestBase.CreateContext(_connection);

        _positions = new StaffPositionService(
            _context,
            BusinessOperationsTestBase.CreateScopeResolver(_context),
            new PunchedApi.Application.Authorization.PermissionService(
                BusinessOperationsTestBase.CreatePermissionService(_context)),
            TestHelpers.CreateLogger<StaffPositionService>());

        _owner = BookingTestBase.CreateOwner("pos-owner@test.com");
        _business = BookingTestBase.CreateBusiness(_owner.Id, "Position Salon");
        _staff = BookingTestBase.CreateStaff(_business.Id, "pos-staff@test.com");

        _otherOwner = BookingTestBase.CreateOwner("pos-other@test.com");
        _otherBusiness = BookingTestBase.CreateBusiness(_otherOwner.Id, "Other Salon");
        _otherStaff = BookingTestBase.CreateStaff(_otherBusiness.Id, "pos-other-staff@test.com");

        await BookingTestBase.SeedAsync(_context, _owner, _business, _staff, _otherOwner, _otherBusiness, _otherStaff);

        await BusinessOperationsTestBase.ProvisionAsync(_context, _business.Id);
        await BusinessOperationsTestBase.ProvisionAsync(_context, _otherBusiness.Id);
    }

    public Task DisposeAsync()
    {
        _context.Dispose();
        _connection.Dispose();
        return Task.CompletedTask;
    }
}

public partial class StaffPositionTests
{
    private Task<ApiResponse<StaffPositionResponse>> CreateAsync(string name) =>
        _positions.CreatePositionAsync(_owner.Id, "Business", new CreateStaffPositionRequest { Name = name });

    // ===========================================================
    //  CRUD AND ORDERING
    // ===========================================================

    [Theory]
    [InlineData("Senior Stylist")]
    [InlineData("Junior Stylist")]
    [InlineData("Master Barber")]
    [InlineData("Tattoo Artist")]
    [InlineData("Piercer")]
    [InlineData("Massage Therapist")]
    [InlineData("Nail Technician")]
    [InlineData("Receptionist")]
    [InlineData("Manager")]
    public async Task Custom_titles_are_accepted_without_a_fixed_global_enum(string title)
    {
        var created = await CreateAsync(title);

        Assert.True(created.Success);
        Assert.Equal(title, created.Data!.Name);
        Assert.True(created.Data.IsActive);
        Assert.Equal(0, created.Data.AssignedStaffCount);
    }

    [Fact]
    public async Task A_position_name_is_required()
    {
        Assert.Equal("VALIDATION_ERROR", (await CreateAsync("  ")).Error?.Code);
    }

    [Fact]
    public async Task Position_names_are_unique_per_business_case_insensitively()
    {
        await CreateAsync("Senior Stylist");

        Assert.Equal("POSITION_NAME_EXISTS", (await CreateAsync("senior stylist")).Error?.Code);
    }

    [Fact]
    public async Task Positions_are_listed_in_display_order()
    {
        await CreateAsync("First");
        await CreateAsync("Second");
        await CreateAsync("Third");

        var list = await _positions.GetPositionsAsync(_owner.Id, "Business");

        Assert.Equal(new[] { "First", "Second", "Third" }, list.Data!.Select(p => p.Name));
    }

    [Fact]
    public async Task Reordering_is_deterministic()
    {
        var a = await CreateAsync("A");
        var b = await CreateAsync("B");
        var c = await CreateAsync("C");

        var reordered = await _positions.ReorderPositionsAsync(_owner.Id, "Business",
            new ReorderStaffPositionsRequest { OrderedIds = new[] { b.Data!.Id, c.Data!.Id, a.Data!.Id } });

        Assert.Equal(new[] { "B", "C", "A" }, reordered.Data!.Select(p => p.Name));
        Assert.Equal(new[] { 0, 1, 2 }, reordered.Data.Select(p => p.SortOrder));
    }

    [Fact]
    public async Task Renaming_a_position_does_not_rewrite_its_history()
    {
        var created = await CreateAsync("Senior Stylist");
        await _positions.AssignStaffPositionAsync(_owner.Id, "Business", _staff.Id,
            new AssignStaffPositionRequest { PositionId = created.Data!.Id });

        await _positions.UpdatePositionAsync(_owner.Id, "Business", created.Data.Id,
            new UpdateStaffPositionRequest { Name = "Master Stylist" });

        var history = await _positions.GetPositionHistoryAsync(_owner.Id, "Business", _staff.Id);

        // The snapshot keeps the label the person actually held.
        Assert.Single(history.Data!);
        Assert.Equal("Senior Stylist", history.Data![0].PositionName);
    }
}

public partial class StaffPositionTests
{
    // ===========================================================
    //  ASSIGNMENT AND HISTORY
    // ===========================================================

    [Fact]
    public async Task Assigning_a_position_updates_the_membership_and_appends_history()
    {
        var created = await CreateAsync("Senior Stylist");

        var assigned = await _positions.AssignStaffPositionAsync(_owner.Id, "Business", _staff.Id,
            new AssignStaffPositionRequest { PositionId = created.Data!.Id, Note = "Promoted" });

        Assert.True(assigned.Success);
        Assert.Equal(created.Data.Id, assigned.Data!.PositionId);
        Assert.Equal("Senior Stylist", assigned.Data.PositionName);
        Assert.True(assigned.Data.PositionIsActive);

        var membership = await _context.BusinessMemberships.AsNoTracking()
            .SingleAsync(m => m.BusinessId == _business.Id && m.UserId == _staff.Id);
        Assert.Equal(created.Data.Id, membership.PositionId);

        var history = await _context.StaffPositionHistory.AsNoTracking()
            .Where(h => h.BusinessId == _business.Id && h.StaffUserId == _staff.Id).ToListAsync();
        Assert.Single(history);
        Assert.Equal("Senior Stylist", history[0].PositionName);
        Assert.Equal(_owner.Id, history[0].ChangedByUserId);
        Assert.Equal("Promoted", history[0].Note);
    }

    [Fact]
    public async Task Reassigning_a_position_appends_a_second_history_entry()
    {
        var junior = await CreateAsync("Junior Stylist");
        var senior = await CreateAsync("Senior Stylist");

        await _positions.AssignStaffPositionAsync(_owner.Id, "Business", _staff.Id,
            new AssignStaffPositionRequest { PositionId = junior.Data!.Id });
        await _positions.AssignStaffPositionAsync(_owner.Id, "Business", _staff.Id,
            new AssignStaffPositionRequest { PositionId = senior.Data!.Id });

        var history = await _positions.GetPositionHistoryAsync(_owner.Id, "Business", _staff.Id);

        Assert.Equal(2, history.Data!.Count);
        Assert.Contains(history.Data, h => h.PositionName == "Junior Stylist");
        Assert.Contains(history.Data, h => h.PositionName == "Senior Stylist");
    }

    [Fact]
    public async Task Re_assigning_the_same_position_is_an_idempotent_no_op()
    {
        var created = await CreateAsync("Senior Stylist");
        await _positions.AssignStaffPositionAsync(_owner.Id, "Business", _staff.Id,
            new AssignStaffPositionRequest { PositionId = created.Data!.Id });

        await _positions.AssignStaffPositionAsync(_owner.Id, "Business", _staff.Id,
            new AssignStaffPositionRequest { PositionId = created.Data.Id });

        // A retried request must not pollute the audit trail.
        Assert.Equal(1, await _context.StaffPositionHistory.CountAsync(h => h.StaffUserId == _staff.Id));
    }

    [Fact]
    public async Task Clearing_a_position_appends_history_and_nulls_the_membership()
    {
        var created = await CreateAsync("Senior Stylist");
        await _positions.AssignStaffPositionAsync(_owner.Id, "Business", _staff.Id,
            new AssignStaffPositionRequest { PositionId = created.Data!.Id });

        var cleared = await _positions.AssignStaffPositionAsync(_owner.Id, "Business", _staff.Id,
            new AssignStaffPositionRequest { Clear = true });

        Assert.Null(cleared.Data!.PositionId);

        var membership = await _context.BusinessMemberships.AsNoTracking()
            .SingleAsync(m => m.BusinessId == _business.Id && m.UserId == _staff.Id);
        Assert.Null(membership.PositionId);

        Assert.Equal(2, await _context.StaffPositionHistory.CountAsync(h => h.StaffUserId == _staff.Id));
    }

    [Fact]
    public async Task History_survives_deleting_the_position()
    {
        var created = await CreateAsync("Senior Stylist");
        await _positions.AssignStaffPositionAsync(_owner.Id, "Business", _staff.Id,
            new AssignStaffPositionRequest { PositionId = created.Data!.Id });

        // Clear the holder so deletion is permitted, then delete the position.
        await _positions.AssignStaffPositionAsync(_owner.Id, "Business", _staff.Id,
            new AssignStaffPositionRequest { Clear = true });
        Assert.True((await _positions.DeletePositionAsync(_owner.Id, "Business", created.Data.Id)).Success);

        var history = await _positions.GetPositionHistoryAsync(_owner.Id, "Business", _staff.Id);

        Assert.Equal(2, history.Data!.Count);
        Assert.Contains(history.Data, h => h.PositionName == "Senior Stylist");
    }
}

public partial class StaffPositionTests
{
    // ===========================================================
    //  DEACTIVATION AND DELETION
    // ===========================================================

    [Fact]
    public async Task Deactivating_a_position_keeps_assignments_and_history_readable()
    {
        var created = await CreateAsync("Senior Stylist");
        await _positions.AssignStaffPositionAsync(_owner.Id, "Business", _staff.Id,
            new AssignStaffPositionRequest { PositionId = created.Data!.Id });

        var deactivated = await _positions.UpdatePositionAsync(_owner.Id, "Business", created.Data.Id,
            new UpdateStaffPositionRequest { IsActive = false });
        Assert.False(deactivated.Data!.IsActive);

        var assignment = await _positions.GetStaffPositionAsync(_owner.Id, "Business", _staff.Id);
        Assert.Equal("Senior Stylist", assignment.Data!.PositionName);
        Assert.False(assignment.Data.PositionIsActive);

        // A deactivated position disappears from the picker list only.
        var active = await _positions.GetPositionsAsync(_owner.Id, "Business", includeInactive: false);
        Assert.Empty(active.Data!);
    }

    [Fact]
    public async Task A_position_someone_holds_cannot_be_deleted()
    {
        var created = await CreateAsync("Senior Stylist");
        await _positions.AssignStaffPositionAsync(_owner.Id, "Business", _staff.Id,
            new AssignStaffPositionRequest { PositionId = created.Data!.Id });

        var refused = await _positions.DeletePositionAsync(_owner.Id, "Business", created.Data.Id);

        Assert.Equal("POSITION_IN_USE", refused.Error?.Code);
        Assert.True(await _context.StaffPositions.AnyAsync(p => p.Id == created.Data.Id));
    }

    [Fact]
    public async Task An_unheld_position_can_be_deleted()
    {
        var created = await CreateAsync("Unused Title");

        Assert.True((await _positions.DeletePositionAsync(_owner.Id, "Business", created.Data!.Id)).Success);
        Assert.False(await _context.StaffPositions.AnyAsync(p => p.Id == created.Data.Id));
    }

    [Fact]
    public async Task The_assigned_staff_count_drives_the_position_list()
    {
        var created = await CreateAsync("Senior Stylist");
        await _positions.AssignStaffPositionAsync(_owner.Id, "Business", _staff.Id,
            new AssignStaffPositionRequest { PositionId = created.Data!.Id });

        var list = await _positions.GetPositionsAsync(_owner.Id, "Business");

        Assert.Equal(1, list.Data!.Single(p => p.Id == created.Data.Id).AssignedStaffCount);
    }

    // ===========================================================
    //  TENANT ISOLATION
    // ===========================================================

    [Fact]
    public async Task Another_business_position_is_indistinguishable_from_a_missing_one()
    {
        var foreign = await _positions.CreatePositionAsync(_otherOwner.Id, "Business",
            new CreateStaffPositionRequest { Name = "Their Title" });

        Assert.Equal("NOT_FOUND", (await _positions.GetPositionAsync(_owner.Id, "Business", foreign.Data!.Id)).Error?.Code);
        Assert.Equal("NOT_FOUND", (await _positions.UpdatePositionAsync(_owner.Id, "Business", foreign.Data.Id,
            new UpdateStaffPositionRequest { Name = "Stolen" })).Error?.Code);
        Assert.Equal("NOT_FOUND", (await _positions.DeletePositionAsync(_owner.Id, "Business", foreign.Data.Id)).Error?.Code);
    }

    [Fact]
    public async Task A_staff_member_cannot_receive_another_business_position()
    {
        var foreign = await _positions.CreatePositionAsync(_otherOwner.Id, "Business",
            new CreateStaffPositionRequest { Name = "Their Title" });

        var attempt = await _positions.AssignStaffPositionAsync(_owner.Id, "Business", _staff.Id,
            new AssignStaffPositionRequest { PositionId = foreign.Data!.Id });

        Assert.Equal("POSITION_NOT_FOUND", attempt.Error?.Code);
        Assert.Null((await _context.BusinessMemberships.AsNoTracking()
            .SingleAsync(m => m.BusinessId == _business.Id && m.UserId == _staff.Id)).PositionId);
    }

    [Fact]
    public async Task A_staff_member_of_another_business_cannot_be_assigned_a_position()
    {
        var created = await CreateAsync("Senior Stylist");

        var attempt = await _positions.AssignStaffPositionAsync(_owner.Id, "Business", _otherStaff.Id,
            new AssignStaffPositionRequest { PositionId = created.Data!.Id });

        Assert.Equal("STAFF_NOT_FOUND", attempt.Error?.Code);
    }

    [Fact]
    public async Task A_staff_caller_only_sees_their_own_history()
    {
        var created = await CreateAsync("Senior Stylist");
        await _positions.AssignStaffPositionAsync(_owner.Id, "Business", _staff.Id,
            new AssignStaffPositionRequest { PositionId = created.Data!.Id });

        var other = await _positions.GetPositionHistoryAsync(_staff.Id, "Staff", _owner.Id);
        Assert.Equal("FORBIDDEN", other.Error?.Code);

        var own = await _positions.GetPositionHistoryAsync(_staff.Id, "Staff", null);
        Assert.Single(own.Data!);
    }

    // ===========================================================
    //  A TITLE IS NOT A GRANT
    // ===========================================================

    [Fact]
    public async Task Holding_a_position_grants_no_permission()
    {
        var manager = await CreateAsync("Manager");
        await _positions.AssignStaffPositionAsync(_owner.Id, "Business", _staff.Id,
            new AssignStaffPositionRequest { PositionId = manager.Data!.Id });

        var permissions = BusinessOperationsTestBase.CreatePermissionService(_context);
        var effective = await permissions.GetEffectivePermissionsAsync(_staff.Id, _business.Id);

        // The staff member keeps exactly the service-professional set: the title
        // "Manager" adds nothing.
        Assert.DoesNotContain("roles.manage", effective);
        Assert.DoesNotContain("staff.manage", effective);
        Assert.DoesNotContain("commissions.payout", effective);
        Assert.DoesNotContain("settings.manage", effective);
        Assert.Contains("appointments.view", effective);
    }
}
