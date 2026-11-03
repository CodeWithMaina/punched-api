using Microsoft.Data.Sqlite;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Tests;

/// <summary>
/// Tests for the three public storefront read services added alongside the
/// storefront completion work: gallery, opening hours and search.
///
/// <para>These are the read paths an anonymous visitor reaches, so the
/// assertions are about what must NOT leak (other tenants' rows, unpublished
/// services, private staff, processing media) as much as what must be returned.
/// SQLite in-memory keeps them fast and provider-independent.</para>
/// </summary>
public class StorefrontPublicReadTests
{
    // ── Gallery ─────────────────────────────────────────────────

    private static StorefrontGalleryService Gallery(ApplicationDbContext context, params string[] modules) =>
        new(context, new StubModuleEntitlements(modules), TestHelpers.CreateLogger<StorefrontGalleryService>());

    private static Media GalleryMedia(Guid businessId, Guid uploaderId, MediaStatus status, string url = "https://cdn.example/a.webp") => new()
    {
        Id = Guid.NewGuid(),
        BusinessId = businessId,
        UploadedByUserId = uploaderId,
        Purpose = MediaPurposes.BusinessGallery,
        SourceKey = $"private/source/{Guid.NewGuid():N}",
        Status = status,
        Visibility = MediaVisibility.Private,
        Width = 1280,
        Height = 960,
        VariantsJson =
            $"[{{\"url\":\"{url}\",\"width\":1280,\"height\":960,\"format\":\"webp\",\"transform\":\"w1280\"}}]"
    };

    [Fact]
    public async Task Gallery_ReturnsOnlyAttachedReadySameBusinessGalleryMedia_InOrder()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var owner = BookingTestBase.CreateOwner();
        var business = BookingTestBase.CreateBusiness(owner.Id);
        await BookingTestBase.SeedAsync(context, owner, business);

        var first = GalleryMedia(business.Id, owner.Id, MediaStatus.Ready, "https://cdn.example/1.webp");
        var second = GalleryMedia(business.Id, owner.Id, MediaStatus.Ready, "https://cdn.example/2.webp");
        var processing = GalleryMedia(business.Id, owner.Id, MediaStatus.Processing, "https://cdn.example/3.webp");
        var unattached = GalleryMedia(business.Id, owner.Id, MediaStatus.Ready, "https://cdn.example/4.webp");
        var wrongPurpose = GalleryMedia(business.Id, owner.Id, MediaStatus.Ready);
        wrongPurpose.Purpose = MediaPurposes.BusinessCover;

        // Another business's gallery image must never appear.
        var otherOwner = BookingTestBase.CreateOwner("other@test.com");
        var otherBusiness = BookingTestBase.CreateBusiness(otherOwner.Id, "Other");
        var foreign = GalleryMedia(otherBusiness.Id, otherOwner.Id, MediaStatus.Ready, "https://cdn.example/foreign.webp");
        await BookingTestBase.SeedAsync(context, otherOwner, otherBusiness);

        context.Media.AddRange(first, second, processing, unattached, wrongPurpose, foreign);
        context.BusinessMedia.AddRange(
            new BusinessMedia { BusinessId = business.Id, MediaId = second.Id, SortOrder = 1 },
            new BusinessMedia { BusinessId = business.Id, MediaId = first.Id, SortOrder = 0, IsFeatured = true },
            new BusinessMedia { BusinessId = business.Id, MediaId = processing.Id, SortOrder = 2 },
            new BusinessMedia { BusinessId = business.Id, MediaId = wrongPurpose.Id, SortOrder = 3 },
            new BusinessMedia { BusinessId = otherBusiness.Id, MediaId = foreign.Id, SortOrder = 0 });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var result = await Gallery(context, "gallery").GetPublicGalleryAsync(business.Id);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(new[] { first.Id, second.Id }, result.Data!.Images.Select(image => image.MediaId));
        Assert.True(result.Data.Images[0].Featured);
        Assert.Single(result.Data.Images[0].Variants);
        Assert.DoesNotContain(result.Data.Images, image => image.MediaId == unattached.Id);
        Assert.DoesNotContain(result.Data.Images, image => image.MediaId == foreign.Id);
    }

    [Fact]
    public async Task Gallery_IsModuleGated_AndUnknownBusinessIsNotFound()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var owner = BookingTestBase.CreateOwner();
        var business = BookingTestBase.CreateBusiness(owner.Id);
        await BookingTestBase.SeedAsync(context, owner, business);

        var disabled = await Gallery(context).GetPublicGalleryAsync(business.Id);
        Assert.Equal("MODULE_DISABLED", disabled.Error?.Code);

        var unknown = await Gallery(context, "gallery").GetPublicGalleryAsync(Guid.NewGuid());
        Assert.Equal("NOT_FOUND", unknown.Error?.Code);
    }

    // ── Opening hours ───────────────────────────────────────────

    private static StorefrontHoursService Hours(ApplicationDbContext context) =>
        new(context, TestHelpers.CreateLogger<StorefrontHoursService>());

    private static StaffAvailability Pattern(
        Guid businessId, Guid staffUserId, int dayOfWeek, int start, int end,
        int? breakStart = null, int? breakEnd = null) => new()
    {
        Id = Guid.NewGuid(),
        BusinessId = businessId,
        StaffUserId = staffUserId,
        DayOfWeek = dayOfWeek,
        IsWorking = true,
        StartMinute = start,
        EndMinute = end,
        BreakStartMinute = breakStart,
        BreakEndMinute = breakEnd,
        UpdatedAt = DateTime.UtcNow
    };

    [Fact]
    public async Task Hours_WithoutAnyRoster_FallBackToTheBusinessBookingWindow()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var owner = BookingTestBase.CreateOwner();
        var business = BookingTestBase.CreateBusiness(owner.Id);
        await BookingTestBase.SeedAsync(context, owner, business);

        var result = await Hours(context).GetPublicOpeningHoursAsync(business.Id);

        Assert.True(result.Success, result.Error?.Message);
        Assert.False(result.Data!.HasStaffSchedule);
        Assert.Equal(7, result.Data.Days.Count);
        Assert.All(result.Data.Days, day =>
        {
            Assert.False(day.IsClosed);
            var period = Assert.Single(day.Periods);
            Assert.Equal("09:00", period.OpensAt);
            Assert.Equal("18:00", period.ClosesAt);
        });
    }

    [Fact]
    public async Task Hours_WithRoster_UnionPerDay_SharedBreak_AndClosedDays()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var owner = BookingTestBase.CreateOwner();
        var business = BookingTestBase.CreateBusiness(owner.Id);
        var first = BookingTestBase.CreateStaff(business.Id, "one@test.com");
        var second = BookingTestBase.CreateStaff(business.Id, "two@test.com");
        await BookingTestBase.SeedAsync(context, owner, business, first, second);

        // Two members, identical Monday shift with the same break (Monday = 1).
        await BookingTestBase.SeedAsync(context,
            Pattern(business.Id, first.Id, 1, 540, 1020, 780, 840),
            Pattern(business.Id, second.Id, 1, 540, 1020, 780, 840),
            // One member works Tuesday mornings only.
            Pattern(business.Id, first.Id, 2, 540, 720));
        context.ChangeTracker.Clear();

        var result = await Hours(context).GetPublicOpeningHoursAsync(business.Id);
        Assert.True(result.Success, result.Error?.Message);

        var days = result.Data!.Days;
        Assert.True(result.Data.HasStaffSchedule);

        var monday = days.Single(day => day.DayOfWeek == 1);
        var mondayPeriod = Assert.Single(monday.Periods);
        Assert.Equal("09:00", mondayPeriod.OpensAt);
        Assert.Equal("17:00", mondayPeriod.ClosesAt);
        // Both shifts declare the same break, so it is a business-wide break.
        Assert.Equal("13:00", mondayPeriod.BreakStartsAt);
        Assert.Equal("14:00", mondayPeriod.BreakEndsAt);

        var tuesday = days.Single(day => day.DayOfWeek == 2);
        var tuesdayPeriod = Assert.Single(tuesday.Periods);
        Assert.Equal("09:00", tuesdayPeriod.OpensAt);
        Assert.Equal("12:00", tuesdayPeriod.ClosesAt);
        Assert.Null(tuesdayPeriod.BreakStartsAt);

        // Nobody works Sunday (0).
        Assert.True(days.Single(day => day.DayOfWeek == 0).IsClosed);
    }

    [Fact]
    public async Task Hours_WholeTeamDayOff_IsReportedAsAClosure()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var owner = BookingTestBase.CreateOwner();
        var business = BookingTestBase.CreateBusiness(owner.Id);
        var first = BookingTestBase.CreateStaff(business.Id, "one@test.com");
        var second = BookingTestBase.CreateStaff(business.Id, "two@test.com");
        await BookingTestBase.SeedAsync(context, owner, business, first, second);

        var holiday = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(10);
        var offOne = BookingTestBase.CreateShift(business.Id, first.Id, holiday, 0, 0, isWorking: false);
        offOne.Note = "Public holiday";
        var offTwo = BookingTestBase.CreateShift(business.Id, second.Id, holiday, 0, 0, isWorking: false);
        offTwo.Note = "Public holiday";
        await BookingTestBase.SeedAsync(context, offOne, offTwo);
        context.ChangeTracker.Clear();

        var result = await Hours(context).GetPublicOpeningHoursAsync(business.Id);

        Assert.True(result.Success, result.Error?.Message);
        var closure = Assert.Single(result.Data!.SpecialHours);
        Assert.Equal(holiday.ToString("yyyy-MM-dd"), closure.Date);
        Assert.True(closure.IsClosed);
        Assert.Equal("Public holiday", closure.Label);
    }

    // ── Search ──────────────────────────────────────────────────

    private static StorefrontSearchService Search(ApplicationDbContext context, params string[] modules) =>
        new(context, new StubModuleEntitlements(modules), TestHelpers.CreateLogger<StorefrontSearchService>());

    [Fact]
    public async Task Search_FindsPublishedServices_ExcludesUnpublishedAndOtherBusiness()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var owner = BookingTestBase.CreateOwner();
        var business = BookingTestBase.CreateBusiness(owner.Id);
        await BookingTestBase.SeedAsync(context, owner, business);

        var visible = BookingTestBase.CreateService(business.Id, "Deluxe Fade", 45, 900m);
        var inactive = BookingTestBase.CreateService(business.Id, "Fade Secret", 30, 400m);
        inactive.IsActive = false;
        var unadvertised = BookingTestBase.CreateService(business.Id, "Fade Preview", 30, 400m);
        unadvertised.Showcase = false;
        var archived = BookingTestBase.CreateService(business.Id, "Fade Archive", 30, 400m);
        archived.IsActive = false;
        archived.IsArchived = true;

        var otherOwner = BookingTestBase.CreateOwner("rival@test.com");
        var rival = BookingTestBase.CreateBusiness(otherOwner.Id, "Rival");
        var foreign = BookingTestBase.CreateService(rival.Id, "Fade Rival", 30, 400m);

        await BookingTestBase.SeedAsync(context, visible, inactive, unadvertised, archived, otherOwner, rival, foreign);
        context.ChangeTracker.Clear();

        var result = await Search(context, "serviceCatalog", "staff").SearchAsync(business.Id, "fade", "all", 1, 12);

        Assert.True(result.Success, result.Error?.Message);
        var hit = Assert.Single(result.Data!.Items);
        Assert.Equal("service", hit.Type);
        Assert.Equal(visible.Id, hit.Id);
        Assert.Equal(900m, hit.Price);
    }

    [Fact]
    public async Task Search_FindsPublicStaffByBioAndTitle_ExcludesPrivateStaff()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var owner = BookingTestBase.CreateOwner();
        var business = BookingTestBase.CreateBusiness(owner.Id);
        var visible = BookingTestBase.CreateStaff(business.Id, "zawadi@test.com");
        visible.FullName = "Zawadi Mwangi";
        visible.BusinessMemberships.First().Bio = "Detail-obsessed nail artist";

        var hidden = BookingTestBase.CreateStaff(business.Id, "hidden@test.com");
        hidden.FullName = "Hidden Person";
        hidden.BusinessMemberships.First().IsPublic = false;

        await BookingTestBase.SeedAsync(context, owner, business, visible, hidden);
        await BookingTestBase.SeedAsync(context, new StaffPositionHistory
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            StaffUserId = visible.Id,
            PositionName = "Nail Artist",
            ChangedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        });
        context.ChangeTracker.Clear();

        var byName = await Search(context, "staff").SearchAsync(business.Id, "zawadi", "all", 1, 12);
        var nameHit = Assert.Single(byName.Data!.Items);
        Assert.Equal("staff", nameHit.Type);
        Assert.Equal(visible.Id, nameHit.Id);
        Assert.Equal("Nail Artist", nameHit.Subtitle);

        // "nail" matches the biography and the job title.
        var byBio = await Search(context, "staff").SearchAsync(business.Id, "nail", "all", 1, 12);
        Assert.Contains(byBio.Data!.Items, item => item.Id == visible.Id);

        // A non-public member is never returned.
        var hiddenQuery = await Search(context, "staff").SearchAsync(business.Id, "hidden", "all", 1, 12);
        Assert.Empty(hiddenQuery.Data!.Items);
    }

    [Fact]
    public async Task Search_BlankQuery_ReturnsCategoriesWithoutItems_AndTypeFilterNarrows()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var owner = BookingTestBase.CreateOwner();
        var business = BookingTestBase.CreateBusiness(owner.Id);
        await BookingTestBase.SeedAsync(context, owner, business);

        await BookingTestBase.SeedAsync(context, new ServiceCategory
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            Name = "Haircuts",
            IsActive = true,
            UpdatedAt = DateTime.UtcNow
        });

        var staff = BookingTestBase.CreateStaff(business.Id, "fade@test.com");
        staff.FullName = "Fade Specialist";
        await BookingTestBase.SeedAsync(context, staff);
        context.ChangeTracker.Clear();

        var blank = await Search(context, "serviceCatalog", "staff").SearchAsync(business.Id, "", "all", 1, 12);
        Assert.Empty(blank.Data!.Items);
        Assert.Equal(new[] { "Haircuts" }, blank.Data.Categories);

        var servicesOnly = await Search(context, "serviceCatalog", "staff")
            .SearchAsync(business.Id, "fade", "services", 1, 12);
        Assert.Empty(servicesOnly.Data!.Items);

        var teamOnly = await Search(context, "serviceCatalog", "staff")
            .SearchAsync(business.Id, "fade", "team", 1, 12);
        var teamHit = Assert.Single(teamOnly.Data!.Items);
        Assert.Equal("staff", teamHit.Type);
    }

    [Fact]
    public async Task Search_RespectsModuleEntitlements()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var owner = BookingTestBase.CreateOwner();
        var business = BookingTestBase.CreateBusiness(owner.Id);
        await BookingTestBase.SeedAsync(context, owner, business);
        await BookingTestBase.SeedAsync(context, BookingTestBase.CreateService(business.Id, "Deluxe Fade", 45, 900m));
        context.ChangeTracker.Clear();

        // Neither the catalogue nor the staff module is enabled.
        var result = await Search(context).SearchAsync(business.Id, "fade", "all", 1, 12);

        Assert.True(result.Success, result.Error?.Message);
        Assert.Empty(result.Data!.Items);
    }
}
