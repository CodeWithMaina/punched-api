using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Tests;

/// <summary>
/// Service category management: CRUD, deterministic ordering, deactivation vs
/// deletion, service assignment (including cross-business rejection), the
/// business-type starter templates and the public grouped catalogue.
/// </summary>
public partial class ServiceCategoryTests : IAsyncLifetime
{
    private Microsoft.Data.Sqlite.SqliteConnection _connection = null!;
    private ApplicationDbContext _context = null!;
    private ServiceCategoryService _categories = null!;
    private ServiceCatalogService _catalog = null!;

    private User _owner = null!;
    private Business _business = null!;
    private User _otherOwner = null!;
    private Business _otherBusiness = null!;

    public async Task InitializeAsync()
    {
        _connection = BookingTestBase.CreateConnection();
        _context = BookingTestBase.CreateContext(_connection);

        _categories = new ServiceCategoryService(
            _context,
            BusinessOperationsTestBase.CreateScopeResolver(_context),
            new PunchedApi.Application.Authorization.PermissionService(
                BusinessOperationsTestBase.CreatePermissionService(_context)),
            new StubModuleEntitlements("serviceCatalog"),
            TestHelpers.CreateLogger<ServiceCategoryService>());

        _catalog = BookingTestBase.CreateCatalogService(_context);

        _owner = BookingTestBase.CreateOwner("cat-owner@test.com");
        _business = BookingTestBase.CreateBusiness(_owner.Id, "Category Salon");
        _otherOwner = BookingTestBase.CreateOwner("cat-other@test.com");
        _otherBusiness = BookingTestBase.CreateBusiness(_otherOwner.Id, "Other Salon");

        await BookingTestBase.SeedAsync(_context, _owner, _business, _otherOwner, _otherBusiness);

        // Provision the RBAC roles so the owner holds the catalogue permissions.
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

public partial class ServiceCategoryTests
{
    private Task<ApiResponse<ServiceCategoryResponse>> CreateAsync(string name, int? sortOrder = null) =>
        _categories.CreateCategoryAsync(_owner.Id, "Business",
            new CreateServiceCategoryRequest { Name = name, SortOrder = sortOrder });

    // ===========================================================
    //  CRUD
    // ===========================================================

    [Fact]
    public async Task Creating_a_category_persists_it_for_the_callers_business()
    {
        var created = await CreateAsync("Haircuts");

        Assert.True(created.Success);
        Assert.Equal("Haircuts", created.Data!.Name);
        Assert.Equal(_business.Id, created.Data.BusinessId);
        Assert.True(created.Data.IsActive);
        Assert.Equal(0, created.Data.ServiceCount);
        Assert.True(created.Data.CanDelete);
    }

    [Fact]
    public async Task Category_names_are_required_and_trimmed()
    {
        Assert.Equal("VALIDATION_ERROR", (await CreateAsync("   ")).Error?.Code);

        var created = await CreateAsync("  Braiding  ");
        Assert.Equal("Braiding", created.Data!.Name);
    }

    [Fact]
    public async Task Duplicate_names_are_rejected_case_insensitively()
    {
        await CreateAsync("Haircuts");

        var duplicate = await CreateAsync("haircuts");

        Assert.Equal("CATEGORY_NAME_EXISTS", duplicate.Error?.Code);
        Assert.Equal(1, await _context.ServiceCategories.CountAsync(c => c.BusinessId == _business.Id));
    }

    [Fact]
    public async Task An_omitted_sort_order_appends_to_the_end()
    {
        await CreateAsync("First");
        await CreateAsync("Second");
        await CreateAsync("Third");

        var list = await _categories.GetCategoriesAsync(_owner.Id, "Business");

        Assert.Equal(new[] { "First", "Second", "Third" }, list.Data!.Select(c => c.Name));
        Assert.Equal(new[] { 0, 1, 2 }, list.Data.Select(c => c.SortOrder));
    }

    [Fact]
    public async Task Updating_renames_a_category_and_rejects_a_clashing_name()
    {
        await CreateAsync("Haircuts");
        var second = await CreateAsync("Colouring");

        var renamed = await _categories.UpdateCategoryAsync(_owner.Id, "Business", second.Data!.Id,
            new UpdateServiceCategoryRequest { Name = "Hair Colouring", Description = "Colour work." });
        Assert.True(renamed.Success);
        Assert.Equal("Hair Colouring", renamed.Data!.Name);
        Assert.Equal("Colour work.", renamed.Data.Description);

        var clash = await _categories.UpdateCategoryAsync(_owner.Id, "Business", second.Data.Id,
            new UpdateServiceCategoryRequest { Name = "Haircuts" });
        Assert.Equal("CATEGORY_NAME_EXISTS", clash.Error?.Code);
    }
}

public partial class ServiceCategoryTests
{
    // ===========================================================
    //  ACTIVATION, DELETION AND SERVICE INTEGRATION
    // ===========================================================

    [Fact]
    public async Task Deactivating_a_category_keeps_its_service_assignments()
    {
        var category = await CreateAsync("Haircuts");
        var service = BookingTestBase.CreateService(_business.Id, "Cut", 30, 500);
        service.CategoryId = category.Data!.Id;
        await BookingTestBase.SeedAsync(_context, service);

        var deactivated = await _categories.UpdateCategoryAsync(_owner.Id, "Business", category.Data.Id,
            new UpdateServiceCategoryRequest { IsActive = false });
        Assert.False(deactivated.Data!.IsActive);

        var reloaded = await _context.ServiceCatalogItems.AsNoTracking().SingleAsync(s => s.Id == service.Id);
        Assert.Equal(category.Data.Id, reloaded.CategoryId);
    }

    [Fact]
    public async Task A_category_referenced_by_a_service_cannot_be_deleted()
    {
        var category = await CreateAsync("Haircuts");
        var service = BookingTestBase.CreateService(_business.Id, "Cut", 30, 500);
        service.CategoryId = category.Data!.Id;
        await BookingTestBase.SeedAsync(_context, service);

        var refused = await _categories.DeleteCategoryAsync(_owner.Id, "Business", category.Data.Id);

        Assert.Equal("CATEGORY_IN_USE", refused.Error?.Code);
        Assert.True(await _context.ServiceCategories.AnyAsync(c => c.Id == category.Data.Id));

        // The reported service count drives the UI canDelete flag.
        var listed = await _categories.GetCategoryAsync(_owner.Id, "Business", category.Data.Id);
        Assert.Equal(1, listed.Data!.ServiceCount);
        Assert.False(listed.Data.CanDelete);
    }

    [Fact]
    public async Task An_unreferenced_category_can_be_deleted()
    {
        var category = await CreateAsync("Unused");

        var deleted = await _categories.DeleteCategoryAsync(_owner.Id, "Business", category.Data!.Id);

        Assert.True(deleted.Success);
        Assert.False(await _context.ServiceCategories.AnyAsync(c => c.Id == category.Data.Id));
    }

    [Fact]
    public async Task Reordering_rewrites_the_order_deterministically()
    {
        var a = await CreateAsync("A");
        var b = await CreateAsync("B");
        var c = await CreateAsync("C");

        var reordered = await _categories.ReorderCategoriesAsync(_owner.Id, "Business",
            new ReorderServiceCategoriesRequest { OrderedIds = new[] { c.Data!.Id, a.Data!.Id, b.Data!.Id } });

        Assert.True(reordered.Success);
        Assert.Equal(new[] { "C", "A", "B" }, reordered.Data!.Select(x => x.Name));
        Assert.Equal(new[] { 0, 1, 2 }, reordered.Data.Select(x => x.SortOrder));
    }

    [Fact]
    public async Task Reordering_with_another_business_category_is_refused_outright()
    {
        var mine = await CreateAsync("Mine");
        var foreign = await _categories.CreateCategoryAsync(_otherOwner.Id, "Business",
            new CreateServiceCategoryRequest { Name = "Theirs" });

        var refused = await _categories.ReorderCategoriesAsync(_owner.Id, "Business",
            new ReorderServiceCategoriesRequest { OrderedIds = new[] { mine.Data!.Id, foreign.Data!.Id } });

        Assert.Equal("NOT_FOUND", refused.Error?.Code);
    }

    [Fact]
    public async Task Reordering_rejects_duplicate_and_empty_id_lists()
    {
        var a = await CreateAsync("A");

        Assert.Equal("VALIDATION_ERROR", (await _categories.ReorderCategoriesAsync(_owner.Id, "Business",
            new ReorderServiceCategoriesRequest { OrderedIds = Array.Empty<Guid>() })).Error?.Code);

        Assert.Equal("VALIDATION_ERROR", (await _categories.ReorderCategoriesAsync(_owner.Id, "Business",
            new ReorderServiceCategoriesRequest { OrderedIds = new[] { a.Data!.Id, a.Data.Id } })).Error?.Code);
    }
}

public partial class ServiceCategoryTests
{
    // ===========================================================
    //  TENANT ISOLATION
    // ===========================================================

    [Fact]
    public async Task Another_business_category_is_indistinguishable_from_a_missing_one()
    {
        var foreign = await _categories.CreateCategoryAsync(_otherOwner.Id, "Business",
            new CreateServiceCategoryRequest { Name = "Theirs" });

        Assert.Equal("NOT_FOUND", (await _categories.GetCategoryAsync(_owner.Id, "Business", foreign.Data!.Id)).Error?.Code);
        Assert.Equal("NOT_FOUND", (await _categories.UpdateCategoryAsync(_owner.Id, "Business", foreign.Data.Id,
            new UpdateServiceCategoryRequest { Name = "Stolen" })).Error?.Code);
        Assert.Equal("NOT_FOUND", (await _categories.DeleteCategoryAsync(_owner.Id, "Business", foreign.Data.Id)).Error?.Code);
        Assert.Equal("NOT_FOUND", (await _categories.GetCategoryServicesAsync(_owner.Id, "Business", foreign.Data.Id)).Error?.Code);

        Assert.Equal("Theirs", (await _context.ServiceCategories.AsNoTracking().SingleAsync(c => c.Id == foreign.Data.Id)).Name);
    }

    [Fact]
    public async Task Listing_returns_only_the_callers_business_categories()
    {
        await CreateAsync("Mine");
        await _categories.CreateCategoryAsync(_otherOwner.Id, "Business",
            new CreateServiceCategoryRequest { Name = "Theirs" });

        var list = await _categories.GetCategoriesAsync(_owner.Id, "Business");

        Assert.Single(list.Data!);
        Assert.Equal("Mine", list.Data![0].Name);
    }

    [Fact]
    public async Task A_service_cannot_be_filed_under_another_business_category()
    {
        var foreign = await _categories.CreateCategoryAsync(_otherOwner.Id, "Business",
            new CreateServiceCategoryRequest { Name = "Theirs" });

        var attempt = await _catalog.CreateServiceAsync(_owner.Id, new CreateServiceRequest
        {
            Name = "Cut",
            DurationMinutes = 30,
            Price = 500,
            CategoryId = foreign.Data!.Id
        });

        Assert.Equal("CATEGORY_NOT_FOUND", attempt.Error?.Code);
    }

    [Fact]
    public async Task A_service_can_be_filed_moved_and_uncategorised()
    {
        var hair = await CreateAsync("Haircuts");
        var nails = await CreateAsync("Nails");

        var created = await _catalog.CreateServiceAsync(_owner.Id, new CreateServiceRequest
        {
            Name = "Cut",
            DurationMinutes = 30,
            Price = 500,
            CategoryId = hair.Data!.Id
        });
        Assert.Equal(hair.Data.Id, created.Data!.CategoryId);
        Assert.Equal("Haircuts", created.Data.CategoryName);

        var moved = await _catalog.UpdateServiceAsync(_owner.Id, created.Data.Id,
            new UpdateServiceRequest { CategoryId = nails.Data!.Id });
        Assert.Equal(nails.Data.Id, moved.Data!.CategoryId);
        Assert.Equal("Nails", moved.Data.CategoryName);

        var cleared = await _catalog.UpdateServiceAsync(_owner.Id, created.Data.Id,
            new UpdateServiceRequest { ClearCategory = true });
        Assert.Null(cleared.Data!.CategoryId);
        Assert.Null(cleared.Data.CategoryName);
    }

    [Fact]
    public async Task An_update_without_a_category_field_leaves_the_filing_untouched()
    {
        var hair = await CreateAsync("Haircuts");
        var created = await _catalog.CreateServiceAsync(_owner.Id, new CreateServiceRequest
        {
            Name = "Cut", DurationMinutes = 30, Price = 500, CategoryId = hair.Data!.Id
        });

        var updated = await _catalog.UpdateServiceAsync(_owner.Id, created.Data!.Id,
            new UpdateServiceRequest { Price = 750 });

        Assert.Equal(750m, updated.Data!.Price);
        Assert.Equal(hair.Data.Id, updated.Data.CategoryId);
    }
}

public partial class ServiceCategoryTests
{
    // ===========================================================
    //  STARTER TEMPLATES
    // ===========================================================

    [Theory]
    [InlineData("salon", "Haircuts")]
    [InlineData("Hair & Beauty Salon", "Braiding")]
    [InlineData("barbershop", "Beard Grooming")]
    [InlineData("Barber Shop", "Shaving")]
    [InlineData("tattoo studio", "Flash Tattoos")]
    [InlineData("Tattoo Parlour", "Consultations")]
    [InlineData("Day Spa", "Massages")]
    [InlineData("spa", "Facials")]
    public void Starter_templates_resolve_for_known_business_types(string businessType, string expected)
    {
        var template = ServiceCategoryTemplates.For(businessType);

        Assert.NotEmpty(template);
        Assert.Contains(template, c => c.Name == expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("restaurant")]
    [InlineData("gym")]
    public void An_unrecognised_business_type_yields_no_template(string businessType)
    {
        // The platform never guesses labels for a business type it does not know.
        Assert.Empty(ServiceCategoryTemplates.For(businessType));
    }

    [Fact]
    public async Task Applying_the_starter_template_creates_ordered_categories()
    {
        var applied = await _categories.ApplyStarterCategoriesAsync(_owner.Id, "Business");

        Assert.True(applied.Success);
        Assert.Equal(ServiceCategoryTemplates.For(_business.Category).Count, applied.Data!.Count);
        Assert.Equal(0, applied.Data[0].SortOrder);
        Assert.Equal(1, applied.Data[1].SortOrder);
    }

    [Fact]
    public async Task Applying_the_starter_template_twice_does_not_duplicate_or_overwrite()
    {
        var first = await _categories.ApplyStarterCategoriesAsync(_owner.Id, "Business");
        var countAfterFirst = first.Data!.Count;

        var second = await _categories.ApplyStarterCategoriesAsync(_owner.Id, "Business");

        Assert.True(second.Success);
        Assert.Equal(countAfterFirst, second.Data!.Count);
        Assert.Equal(countAfterFirst, await _context.ServiceCategories.CountAsync(c => c.BusinessId == _business.Id));
    }

    [Fact]
    public async Task A_curated_catalogue_is_never_re_seeded()
    {
        await CreateAsync("My Own Category");

        var applied = await _categories.ApplyStarterCategoriesAsync(_owner.Id, "Business");

        Assert.True(applied.Success);
        Assert.Single(applied.Data!);
        Assert.Equal("My Own Category", applied.Data![0].Name);
    }

    [Fact]
    public async Task An_unrecognised_business_type_reports_no_template()
    {
        _business.Category = "restaurant";
        _context.Businesses.Update(_business);
        await _context.SaveChangesAsync();

        var applied = await _categories.ApplyStarterCategoriesAsync(_owner.Id, "Business");

        Assert.Equal("NO_TEMPLATE", applied.Error?.Code);
    }
}

public partial class ServiceCategoryTests
{
    // ===========================================================
    //  PUBLIC GROUPED CATALOGUE
    // ===========================================================

    private async Task<(Guid hairId, Guid nailsId)> SeedGroupedCatalogueAsync()
    {
        var hair = await CreateAsync("Haircuts");
        var nails = await CreateAsync("Nails");

        var cut = BookingTestBase.CreateService(_business.Id, "Cut", 30, 500);
        cut.CategoryId = hair.Data!.Id;
        var blowDry = BookingTestBase.CreateService(_business.Id, "Blow Dry", 20, 300);
        blowDry.CategoryId = hair.Data.Id;
        var manicure = BookingTestBase.CreateService(_business.Id, "Manicure", 45, 800);
        manicure.CategoryId = nails.Data!.Id;
        var hidden = BookingTestBase.CreateService(_business.Id, "Unlisted", 10, 100);
        hidden.IsActive = false;

        await BookingTestBase.SeedAsync(_context, cut, blowDry, manicure, hidden);
        return (hair.Data.Id, nails.Data.Id);
    }

    [Fact]
    public async Task The_public_catalogue_groups_published_services_by_active_category()
    {
        await SeedGroupedCatalogueAsync();

        var catalogue = await _categories.GetPublicCatalogueAsync(_business.Id);

        Assert.True(catalogue.Success);
        Assert.Equal(_business.Id, catalogue.Data!.BusinessId);
        Assert.Equal(2, catalogue.Data.Categories.Count);

        var hair = catalogue.Data.Categories.Single(c => c.Name == "Haircuts");
        Assert.Equal(new[] { "Blow Dry", "Cut" }, hair.Services.Select(s => s.Name));

        var nails = catalogue.Data.Categories.Single(c => c.Name == "Nails");
        Assert.Equal(new[] { "Manicure" }, nails.Services.Select(s => s.Name));

        // The inactive service never reaches the storefront.
        Assert.DoesNotContain(catalogue.Data.Categories.SelectMany(c => c.Services), s => s.Name == "Unlisted");
        Assert.Empty(catalogue.Data.Uncategorized);
    }

    [Fact]
    public async Task A_deactivated_category_leaves_its_services_visible_in_the_uncategorised_bucket()
    {
        var (hairId, _) = await SeedGroupedCatalogueAsync();

        await _categories.UpdateCategoryAsync(_owner.Id, "Business", hairId,
            new UpdateServiceCategoryRequest { IsActive = false });

        var catalogue = await _categories.GetPublicCatalogueAsync(_business.Id);

        Assert.Single(catalogue.Data!.Categories);
        Assert.Equal("Nails", catalogue.Data.Categories[0].Name);

        // Bookable services must never silently disappear from the storefront.
        Assert.Equal(new[] { "Blow Dry", "Cut" }, catalogue.Data.Uncategorized.Select(s => s.Name));
    }

    [Fact]
    public async Task Uncategorised_services_are_returned_in_their_own_group()
    {
        var loose = BookingTestBase.CreateService(_business.Id, "Walk-in Trim", 15, 200);
        await BookingTestBase.SeedAsync(_context, loose);

        var catalogue = await _categories.GetPublicCatalogueAsync(_business.Id);

        Assert.Empty(catalogue.Data!.Categories);
        Assert.Equal(new[] { "Walk-in Trim" }, catalogue.Data.Uncategorized.Select(s => s.Name));
    }

    [Fact]
    public async Task The_public_catalogue_refuses_a_business_that_did_not_buy_the_module()
    {
        var gated = new ServiceCategoryService(
            _context,
            BusinessOperationsTestBase.CreateScopeResolver(_context),
            new PunchedApi.Application.Authorization.PermissionService(
                BusinessOperationsTestBase.CreatePermissionService(_context)),
            new StubModuleEntitlements(),
            TestHelpers.CreateLogger<ServiceCategoryService>());

        var refused = await gated.GetPublicCatalogueAsync(_business.Id);

        Assert.Equal("MODULE_DISABLED", refused.Error?.Code);
    }

    [Fact]
    public async Task The_public_catalogue_never_leaks_another_business()
    {
        await SeedGroupedCatalogueAsync();

        var foreign = await _categories.GetPublicCatalogueAsync(_otherBusiness.Id);

        Assert.True(foreign.Success);
        Assert.Empty(foreign.Data!.Categories);
        Assert.Empty(foreign.Data.Uncategorized);
    }

    [Fact]
    public async Task A_missing_business_is_reported_as_not_found()
    {
        Assert.Equal("NOT_FOUND", (await _categories.GetPublicCatalogueAsync(Guid.NewGuid())).Error?.Code);
    }
}
