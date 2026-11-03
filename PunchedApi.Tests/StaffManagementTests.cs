using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.Authorization;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Tests;

/// <summary>
/// Owner-side staff management: the per-member profile, service assignments, the
/// recurring working week with breaks, dated time off, and activate/deactivate —
/// including the guarantees that matter most: a business can never touch another
/// business's staff, deactivation never destroys history, and what is saved as a
/// working week is what the booking engine actually offers.
/// </summary>
public partial class StaffManagementTests : IAsyncLifetime
{
    private Microsoft.Data.Sqlite.SqliteConnection _connection = null!;
    private ApplicationDbContext _context = null!;
    private StaffManagementService _staff = null!;

    private User _owner = null!;
    private Business _business = null!;
    private User _member = null!;
    private User _customer = null!;
    private ServiceCatalogItem _service = null!;
    private ServiceCatalogItem _otherService = null!;

    private User _otherOwner = null!;
    private Business _otherBusiness = null!;
    private User _otherMember = null!;

    /// <summary>Set to make the permission service deny a specific code.</summary>
    private readonly HashSet<string> _denied = new(StringComparer.Ordinal);

    public async Task InitializeAsync()
    {
        _connection = BookingTestBase.CreateConnection();
        _context = BookingTestBase.CreateContext(_connection);

        var permissions = new PermissionService(BusinessOperationsTestBase.CreatePermissionService(_context));
        _staff = new StaffManagementService(
            _context,
            BusinessOperationsTestBase.CreateScopeResolver(_context),
            new TestPermissionService(permissions, _denied),
            TestHelpers.CreateLogger<StaffManagementService>());

        _owner = BookingTestBase.CreateOwner("sm-owner@test.com");
        _business = BookingTestBase.CreateBusiness(_owner.Id, "Staff Salon");
        _member = BookingTestBase.CreateStaff(_business.Id, "sm-member@test.com");
        _customer = BookingTestBase.CreateCustomer("sm-customer@test.com");

        _otherOwner = BookingTestBase.CreateOwner("sm-other-owner@test.com");
        _otherBusiness = BookingTestBase.CreateBusiness(_otherOwner.Id, "Other Salon");
        _otherMember = BookingTestBase.CreateStaff(_otherBusiness.Id, "sm-other-member@test.com");

        _service = BookingTestBase.CreateService(_business.Id, "Signature Cut", 60, 1500m);
        _otherService = BookingTestBase.CreateService(_otherBusiness.Id, "Other Cut", 30, 900m);

        await BookingTestBase.SeedAsync(
            _context, _owner, _business, _member, _customer, _service,
            _otherOwner, _otherBusiness, _otherMember, _otherService);

        await BusinessOperationsTestBase.ProvisionAsync(_context, _business.Id);
        await BusinessOperationsTestBase.ProvisionAsync(_context, _otherBusiness.Id);

        await _context.StaffServiceAssignments.AddAsync(
            BookingTestBase.CreateAssignment(_business.Id, _member.Id, _service.Id));
        await _context.SaveChangesAsync();
    }

    public Task DisposeAsync()
    {
        _context.Dispose();
        _connection.Dispose();
        return Task.CompletedTask;
    }
}

/// <summary>
/// A permission service that delegates to the real one but can be told to deny a
/// code, so a FORBIDDEN path is asserted against this service's own gate rather
/// than against the seeded role matrix.
/// </summary>
internal sealed class TestPermissionService : IPermissionService
{
    private readonly IPermissionService _inner;
    private readonly HashSet<string> _denied;

    public TestPermissionService(IPermissionService inner, HashSet<string> denied)
    {
        _inner = inner;
        _denied = denied;
    }

    public bool HasPermission(string role, string permissionCode) => _inner.HasPermission(role, permissionCode);

    public Task<bool> CanAsync(System.Security.Claims.ClaimsPrincipal user, string permissionCode) =>
        _inner.CanAsync(user, permissionCode);

    public Task<bool> HasBusinessPermissionAsync(Guid userId, Guid businessId, string permissionCode) =>
        _denied.Contains(permissionCode)
            ? Task.FromResult(false)
            : _inner.HasBusinessPermissionAsync(userId, businessId, permissionCode);
}

/// <summary>Profile reads and partial updates.</summary>
public partial class StaffManagementTests
{
    private Task<ApiResponse<StaffDetailResponse>> DetailAsync(Guid staffId) =>
        _staff.GetStaffDetailAsync(_owner.Id, "Business", staffId);

    private Task<ApiResponse<StaffDetailResponse>> UpdateAsync(UpdateStaffRequest request) =>
        _staff.UpdateStaffAsync(_owner.Id, "Business", _member.Id, request);

    [Fact]
    public async Task Detail_returns_the_members_identity_employment_and_services()
    {
        var result = await DetailAsync(_member.Id);

        Assert.True(result.Success);
        var detail = result.Data!;
        Assert.Equal(_member.Id, detail.UserId);
        Assert.Equal(_member.FullName, detail.FullName);
        Assert.Equal("Active", detail.MembershipStatus);
        Assert.True(detail.IsActive);
        Assert.True(detail.IsPublic);
        Assert.True(detail.IsBookable);

        var service = Assert.Single(detail.Services);
        Assert.Equal(_service.Id, service.ServiceId);
        Assert.Equal(1500m, service.Price);
        Assert.Equal(60, service.DurationMinutes);
    }

    [Fact]
    public async Task Detail_reports_the_working_week_source_honestly()
    {
        // No pattern and no dated rows: the business's own window applies.
        var none = await DetailAsync(_member.Id);
        Assert.Equal("business", none.Data!.AvailabilitySource);

        await _context.StaffShifts.AddAsync(
            BookingTestBase.CreateShift(_business.Id, _member.Id, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1), 9, 17));
        await _context.SaveChangesAsync();

        // Dated rows but no saved pattern: the week is derived from the roster.
        var derived = await DetailAsync(_member.Id);
        Assert.Equal("roster", derived.Data!.AvailabilitySource);

        var saved = await _staff.SetStaffAvailabilityAsync(_owner.Id, "Business", _member.Id,
            new SetStaffAvailabilityRequest
            {
                Days = [new StaffAvailabilityDayInput { DayOfWeek = 1, IsWorking = true, StartMinute = 540, EndMinute = 1020 }]
            });
        Assert.True(saved.Success);

        var pattern = await DetailAsync(_member.Id);
        Assert.Equal("pattern", pattern.Data!.AvailabilitySource);
        Assert.Equal(7, pattern.Data!.Availability.Count);
        Assert.True(pattern.Data!.Availability.Single(d => d.DayOfWeek == 1).IsWorking);
        Assert.False(pattern.Data!.Availability.Single(d => d.DayOfWeek == 2).IsWorking);
    }

    [Fact]
    public async Task Update_touches_only_the_fields_that_were_sent()
    {
        var originalName = _member.FullName;

        var result = await UpdateAsync(new UpdateStaffRequest { Bio = "  Colour specialist.  " });

        Assert.True(result.Success);
        Assert.Equal("Colour specialist.", result.Data!.Bio);
        Assert.Equal(originalName, result.Data!.FullName);
        Assert.Null(result.Data!.PhoneNumber);
    }

    [Fact]
    public async Task Update_clears_a_biography_when_an_empty_string_is_sent()
    {
        await UpdateAsync(new UpdateStaffRequest { Bio = "Something" });

        var cleared = await UpdateAsync(new UpdateStaffRequest { Bio = "   " });

        Assert.True(cleared.Success);
        Assert.Null(cleared.Data!.Bio);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("")]
    public async Task Update_rejects_a_blank_name(string name)
    {
        var result = await UpdateAsync(new UpdateStaffRequest { FullName = name });

        Assert.False(result.Success);
        Assert.Equal("VALIDATION_ERROR", result.Error!.Code);
    }

    [Fact]
    public async Task Update_rejects_an_over_long_biography_and_leaves_it_unchanged()
    {
        await UpdateAsync(new UpdateStaffRequest { Bio = "Keep me" });

        var result = await UpdateAsync(new UpdateStaffRequest { Bio = new string('x', 301) });

        Assert.False(result.Success);
        Assert.Equal("VALIDATION_ERROR", result.Error!.Code);
        Assert.Equal("Keep me", (await DetailAsync(_member.Id)).Data!.Bio);
    }

    [Fact]
    public async Task Update_requires_the_manage_permission()
    {
        _denied.Add("staff.manage");

        var result = await UpdateAsync(new UpdateStaffRequest { Bio = "nope" });

        Assert.False(result.Success);
        Assert.Equal("FORBIDDEN", result.Error!.Code);
    }
}

/// <summary>Position and photo wiring on the same update path.</summary>
public partial class StaffManagementTests
{
    [Fact]
    public async Task Update_assigns_and_clears_a_position()
    {
        var position = new StaffPosition
        {
            Id = Guid.NewGuid(),
            BusinessId = _business.Id,
            Name = "Senior Stylist",
            UpdatedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        };
        await _context.StaffPositions.AddAsync(position);
        await _context.SaveChangesAsync();

        var assigned = await UpdateAsync(new UpdateStaffRequest { PositionId = position.Id });
        Assert.Equal("Senior Stylist", assigned.Data!.PositionName);

        var cleared = await UpdateAsync(new UpdateStaffRequest { ClearPosition = true });
        Assert.Null(cleared.Data!.PositionId);
        Assert.Null(cleared.Data!.PositionName);
    }

    [Fact]
    public async Task Update_rejects_a_position_belonging_to_another_business()
    {
        var foreign = new StaffPosition
        {
            Id = Guid.NewGuid(),
            BusinessId = _otherBusiness.Id,
            Name = "Other Title",
            UpdatedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        };
        await _context.StaffPositions.AddAsync(foreign);
        await _context.SaveChangesAsync();

        var result = await UpdateAsync(new UpdateStaffRequest { PositionId = foreign.Id });

        Assert.False(result.Success);
        Assert.Equal("NOT_FOUND", result.Error!.Code);
    }

    [Fact]
    public async Task Update_stores_a_profile_photo_pointer_and_resolves_its_url()
    {
        var media = new Media
        {
            Id = Guid.NewGuid(),
            BusinessId = _business.Id,
            UploadedByUserId = _owner.Id,
            Purpose = MediaPurposes.UserAvatar,
            Status = MediaStatus.Ready,
            SourceKey = "avatars/one.jpg",
            VariantsJson = """[{"url":"https://cdn.test/320.jpg","width":320,"height":320,"format":"jpeg"}]""",
            CreatedAt = DateTime.UtcNow
        };
        await _context.Media.AddAsync(media);
        await _context.SaveChangesAsync();

        var result = await UpdateAsync(new UpdateStaffRequest { AvatarMediaId = media.Id });

        Assert.True(result.Success);
        Assert.Equal(media.Id, result.Data!.AvatarMediaId);
        Assert.Equal("https://cdn.test/320.jpg", result.Data!.AvatarUrl);
    }

    [Fact]
    public async Task Update_rejects_a_photo_that_does_not_belong_to_the_business()
    {
        var foreign = new Media
        {
            Id = Guid.NewGuid(),
            BusinessId = _otherBusiness.Id,
            UploadedByUserId = _otherOwner.Id,
            Purpose = MediaPurposes.UserAvatar,
            Status = MediaStatus.Ready,
            SourceKey = "avatars/other.jpg",
            CreatedAt = DateTime.UtcNow
        };
        await _context.Media.AddAsync(foreign);
        await _context.SaveChangesAsync();

        var result = await UpdateAsync(new UpdateStaffRequest { AvatarMediaId = foreign.Id });

        Assert.False(result.Success);
        Assert.Equal("NOT_FOUND", result.Error!.Code);
    }
}

/// <summary>Activate / deactivate, and the history it must never destroy.</summary>
public partial class StaffManagementTests
{
    private Task<ApiResponse<StaffStatusResponse>> SetActiveAsync(Guid staffId, bool active) =>
        _staff.SetStaffActiveAsync(_owner.Id, "Business", staffId, active);

    [Fact]
    public async Task Deactivate_suspends_the_membership_and_stops_them_being_bookable()
    {
        var result = await SetActiveAsync(_member.Id, false);

        Assert.True(result.Success);
        Assert.Equal("Suspended", result.Data!.MembershipStatus);
        Assert.False(result.Data!.IsActive);
        Assert.NotNull(result.Data!.EndedAt);

        var membership = await _context.BusinessMemberships
            .SingleAsync(m => m.UserId == _member.Id && m.BusinessId == _business.Id);
        Assert.False(membership.IsBookable);
        Assert.Equal(BusinessMembershipStatus.Suspended, membership.Status);
    }

    [Fact]
    public async Task Deactivate_keeps_every_historical_record()
    {
        var appointment = BookingTestBase.CreateAppointment(
            _business.Id, _customer.Id, _member.Id,
            DateTime.UtcNow.AddDays(-2), DateTime.UtcNow.AddDays(-2).AddMinutes(60));
        await _context.Appointments.AddAsync(appointment);
        await _context.SaveChangesAsync();

        await SetActiveAsync(_member.Id, false);

        Assert.True(await _context.Appointments.AnyAsync(a => a.Id == appointment.Id));
        Assert.True(await _context.StaffServiceAssignments
            .AnyAsync(a => a.BusinessId == _business.Id && a.StaffUserId == _member.Id));

        var detail = await DetailAsync(_member.Id);
        Assert.True(detail.Success);
        Assert.False(detail.Data!.IsActive);
        Assert.Single(detail.Data!.Services);
    }

    [Fact]
    public async Task Activate_restores_the_membership_and_clears_the_end_date()
    {
        await SetActiveAsync(_member.Id, false);

        var result = await SetActiveAsync(_member.Id, true);

        Assert.True(result.Success);
        Assert.Equal("Active", result.Data!.MembershipStatus);
        Assert.True(result.Data!.IsActive);
        Assert.Null(result.Data!.EndedAt);
    }

    [Fact]
    public async Task Repeating_a_transition_is_idempotent()
    {
        await SetActiveAsync(_member.Id, false);
        var again = await SetActiveAsync(_member.Id, false);
        Assert.True(again.Success);
        Assert.Equal("Suspended", again.Data!.MembershipStatus);

        await SetActiveAsync(_member.Id, true);
        var back = await SetActiveAsync(_member.Id, true);
        Assert.True(back.Success);
        Assert.Equal("Active", back.Data!.MembershipStatus);
    }

    [Fact]
    public async Task The_owner_cannot_be_deactivated_from_the_staff_list()
    {
        var result = await SetActiveAsync(_owner.Id, false);

        Assert.False(result.Success);
        Assert.Equal("FORBIDDEN", result.Error!.Code);
        Assert.Equal(
            BusinessMembershipStatus.Active,
            (await _context.BusinessMemberships.SingleAsync(m => m.UserId == _owner.Id)).Status);
    }
}


/// <summary>Service assignments.</summary>
public partial class StaffManagementTests
{
    private Task<ApiResponse<List<StaffServiceLinkResponse>>> SetServicesAsync(params Guid[] serviceIds) =>
        _staff.SetStaffServicesAsync(_owner.Id, "Business", _member.Id, new SetStaffServicesRequest
        {
            ServiceIds = [.. serviceIds]
        });

    /// <summary>A second service that belongs to the caller's own business.</summary>
    private Guid AddOwnedService(string name)
    {
        var service = BookingTestBase.CreateService(_business.Id, name, 20, 400m);
        _context.ServiceCatalogItems.Add(service);
        _context.SaveChanges();
        return service.Id;
    }

    [Fact]
    public async Task Service_assignment_replaces_the_whole_set_in_one_write()
    {
        var added = await SetServicesAsync(_service.Id, AddOwnedService("Beard Trim"));

        Assert.True(added.Success);
        Assert.Equal(2, added.Data!.Count);

        var removed = await SetServicesAsync(_service.Id);

        Assert.True(removed.Success);
        Assert.Single(removed.Data!);
        Assert.Equal(
            1,
            await _context.StaffServiceAssignments.CountAsync(a => a.BusinessId == _business.Id && a.StaffUserId == _member.Id));
    }

    [Fact]
    public async Task Service_assignment_ignores_duplicates_and_empty_ids()
    {
        var result = await SetServicesAsync(_service.Id, _service.Id, Guid.Empty);

        Assert.True(result.Success);
        Assert.Single(result.Data!);
    }

    [Fact]
    public async Task Service_assignment_rejects_a_service_from_another_business_and_changes_nothing()
    {
        var result = await SetServicesAsync(_otherService.Id);

        Assert.False(result.Success);
        Assert.Equal("NOT_FOUND", result.Error!.Code);
        Assert.True(await _context.StaffServiceAssignments
            .AnyAsync(a => a.BusinessId == _business.Id && a.StaffUserId == _member.Id && a.ServiceCatalogItemId == _service.Id));
    }

    [Fact]
    public async Task Service_assignment_requires_the_manage_permission()
    {
        _denied.Add("staff.manage");

        var result = await SetServicesAsync(_service.Id);

        Assert.False(result.Success);
        Assert.Equal("FORBIDDEN", result.Error!.Code);
    }
}


/// <summary>Working week and time off.</summary>
public partial class StaffManagementTests
{
    private Task<ApiResponse<List<StaffAvailabilityDayResponse>>> SetWeekAsync(params StaffAvailabilityDayInput[] days) =>
        _staff.SetStaffAvailabilityAsync(_owner.Id, "Business", _member.Id, new SetStaffAvailabilityRequest
        {
            Days = [.. days]
        });

    [Fact]
    public async Task Working_week_rejects_a_window_that_ends_before_it_starts()
    {
        var result = await SetWeekAsync(new StaffAvailabilityDayInput
        {
            DayOfWeek = 1, IsWorking = true, StartMinute = 1020, EndMinute = 540
        });

        Assert.False(result.Success);
        Assert.Equal("VALIDATION_ERROR", result.Error!.Code);
    }

    [Fact]
    public async Task Working_week_rejects_a_break_outside_the_window()
    {
        var result = await SetWeekAsync(new StaffAvailabilityDayInput
        {
            DayOfWeek = 1, IsWorking = true, StartMinute = 540, EndMinute = 1020,
            BreakStartMinute = 500, BreakEndMinute = 600
        });

        Assert.False(result.Success);
        Assert.Equal("VALIDATION_ERROR", result.Error!.Code);
    }

    [Fact]
    public async Task Working_week_rejects_a_duplicate_weekday()
    {
        var result = await SetWeekAsync(
            new StaffAvailabilityDayInput { DayOfWeek = 1, IsWorking = true, StartMinute = 540, EndMinute = 1020 },
            new StaffAvailabilityDayInput { DayOfWeek = 1, IsWorking = false });

        Assert.False(result.Success);
        Assert.Equal("VALIDATION_ERROR", result.Error!.Code);
    }

    [Fact]
    public async Task Working_week_reports_a_closed_day_as_an_empty_window()
    {
        var result = await SetWeekAsync(
            new StaffAvailabilityDayInput { DayOfWeek = 0, IsWorking = false, StartMinute = 0, EndMinute = 0 },
            new StaffAvailabilityDayInput
            {
                DayOfWeek = 1, IsWorking = true, StartMinute = 540, EndMinute = 1020,
                BreakStartMinute = 780, BreakEndMinute = 840
            });

        Assert.True(result.Success);
        var sunday = result.Data!.Single(d => d.DayOfWeek == 0);
        var monday = result.Data!.Single(d => d.DayOfWeek == 1);
        Assert.False(sunday.IsWorking);
        Assert.Equal(0, sunday.StartMinute);
        Assert.Equal(0, sunday.EndMinute);
        Assert.Equal(780, monday.BreakStartMinute);
        Assert.Equal(840, monday.BreakEndMinute);
    }

    [Fact]
    public async Task Working_week_round_trips_and_clears_back_to_the_derived_week()
    {
        await SetWeekAsync(new StaffAvailabilityDayInput
        {
            DayOfWeek = 3, IsWorking = true, StartMinute = 600, EndMinute = 1080
        });

        var read = await _staff.GetStaffAvailabilityAsync(_owner.Id, "Business", _member.Id);
        Assert.Equal(600, read.Data!.Single(d => d.DayOfWeek == 3).StartMinute);

        var cleared = await _staff.ClearStaffAvailabilityAsync(_owner.Id, "Business", _member.Id);
        Assert.True(cleared.Success);
        Assert.Empty(await _context.StaffAvailability
            .Where(a => a.BusinessId == _business.Id && a.StaffUserId == _member.Id).ToListAsync());
        Assert.Equal("business", (await DetailAsync(_member.Id)).Data!.AvailabilitySource);
    }

    [Fact]
    public async Task Time_off_creates_dated_days_off_and_can_be_removed()
    {
        var from = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(5);
        var to = from.AddDays(2);

        var added = await _staff.AddStaffTimeOffAsync(_owner.Id, "Business", _member.Id,
            new AddStaffTimeOffRequest { From = from, To = to, Reason = "Annual leave" });

        Assert.True(added.Success);
        Assert.Equal(3, added.Data!.Count);
        Assert.All(added.Data!, entry => Assert.False(entry.IsWorking));
        Assert.All(added.Data!, entry => Assert.Equal("Annual leave", entry.Reason));

        var removed = await _staff.RemoveStaffTimeOffAsync(_owner.Id, "Business", _member.Id, from);
        Assert.Equal(2, removed.Data!.Count);

        // The booking engine reads the same rows: a day off is not a working window.
        var shift = await _context.StaffShifts
            .SingleAsync(s => s.BusinessId == _business.Id && s.StaffUserId == _member.Id && s.Date == to);
        Assert.False(shift.IsWorking);
    }

    [Fact]
    public async Task Time_off_rejects_an_inverted_or_over_long_range()
    {
        var from = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(5);

        var inverted = await _staff.AddStaffTimeOffAsync(_owner.Id, "Business", _member.Id,
            new AddStaffTimeOffRequest { From = from, To = from.AddDays(-1) });
        Assert.False(inverted.Success);
        Assert.Equal("VALIDATION_ERROR", inverted.Error!.Code);

        var tooLong = await _staff.AddStaffTimeOffAsync(_owner.Id, "Business", _member.Id,
            new AddStaffTimeOffRequest { From = from, To = from.AddDays(400) });
        Assert.False(tooLong.Success);
        Assert.Equal("VALIDATION_ERROR", tooLong.Error!.Code);
    }
}


/// <summary>
/// Business isolation: a staff id belonging to another business must be
/// indistinguishable from one that does not exist, on every route.
/// </summary>
public partial class StaffManagementTests
{
    [Fact]
    public async Task Another_businesses_staff_member_is_never_reachable()
    {
        var unknown = Guid.NewGuid();

        foreach (var result in new[]
                 {
                     (await _staff.GetStaffDetailAsync(_owner.Id, "Business", _otherMember.Id)).Error,
                     (await _staff.UpdateStaffAsync(_owner.Id, "Business", _otherMember.Id,
                         new UpdateStaffRequest { Bio = "hijack" })).Error,
                     (await _staff.SetStaffActiveAsync(_owner.Id, "Business", _otherMember.Id, false)).Error,
                     (await _staff.GetStaffServicesAsync(_owner.Id, "Business", _otherMember.Id)).Error,
                     (await _staff.SetStaffServicesAsync(_owner.Id, "Business", _otherMember.Id,
                         new SetStaffServicesRequest())).Error,
                     (await _staff.GetStaffAvailabilityAsync(_owner.Id, "Business", _otherMember.Id)).Error,
                     (await _staff.SetStaffAvailabilityAsync(_owner.Id, "Business", _otherMember.Id,
                         new SetStaffAvailabilityRequest())).Error,
                     (await _staff.ClearStaffAvailabilityAsync(_owner.Id, "Business", _otherMember.Id)).Error,
                     (await _staff.AddStaffTimeOffAsync(_owner.Id, "Business", _otherMember.Id,
                         new AddStaffTimeOffRequest())).Error,
                     (await _staff.RemoveStaffTimeOffAsync(_owner.Id, "Business", _otherMember.Id,
                         DateOnly.FromDateTime(DateTime.UtcNow))).Error,
                     (await _staff.GetStaffDetailAsync(_owner.Id, "Business", unknown)).Error,
                 })
        {
            Assert.NotNull(result);
            Assert.Equal("NOT_FOUND", result!.Code);
        }

        // The other business's member is untouched.
        var membership = await _context.BusinessMemberships
            .SingleAsync(m => m.UserId == _otherMember.Id && m.BusinessId == _otherBusiness.Id);
        Assert.Equal(BusinessMembershipStatus.Active, membership.Status);
        Assert.Null(membership.Bio);
    }

    [Fact]
    public async Task A_customer_membership_is_not_a_staff_record()
    {
        await _context.BusinessMemberships.AddAsync(BookingTestBase.CreateMembership(
            _customer.Id, _business.Id, BusinessMembershipRole.Customer));
        await _context.SaveChangesAsync();

        var result = await _staff.GetStaffDetailAsync(_owner.Id, "Business", _customer.Id);

        Assert.False(result.Success);
        Assert.Equal("NOT_FOUND", result.Error!.Code);
    }

    [Fact]
    public async Task A_caller_with_no_business_sees_nothing()
    {
        var stranger = BookingTestBase.CreateOwner("sm-stranger@test.com");
        await _context.Users.AddAsync(stranger);
        await _context.SaveChangesAsync();

        var result = await _staff.GetStaffDetailAsync(stranger.Id, "Business", _member.Id);

        Assert.False(result.Success);
        Assert.Equal("NOT_FOUND", result.Error!.Code);
    }

    [Fact]
    public async Task Reading_a_profile_requires_the_view_permission()
    {
        _denied.Add("staff.view");

        var result = await _staff.GetStaffDetailAsync(_owner.Id, "Business", _member.Id);

        Assert.False(result.Success);
        Assert.Equal("FORBIDDEN", result.Error!.Code);
    }
}
