using Microsoft.Data.Sqlite;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Tests;

/// <summary>
/// The public roster's module gate.
///
/// <para>The storefront's Team navigation is derived from the roster payload
/// (<c>hasTeam = roster.length &gt; 0</c>), and the roster carries staff PROFILE
/// data — name, job title, bio, specialities, photo. <c>[RequireModule]</c>
/// cannot gate it: the endpoint is <c>[AllowAnonymous]</c> (so the filter skips
/// it) and the attribute is single-key, so it could not express two modules
/// anyway. The gate therefore lives in the service, and these tests pin it:
/// withdrawing <c>staff</c> — or the catalogue it sits beside — must make the
/// roster UNAVAILABLE, not merely empty.</para>
///
/// <para>This is the behaviour that was missing: the roster was served with no
/// entitlement check at all, so switching the staff module off platform-wide hid
/// the owner's staff console while every staff profile stayed publicly
/// readable.</para>
/// </summary>
public sealed class StorefrontRosterEntitlementTests
{
    private static async Task<(ApplicationDbContext Context, ServiceCatalogService Service, Business Business)>
        CreateEnvAsync(SqliteConnection connection, params string[] enabledModules)
    {
        var context = BookingTestBase.CreateContext(connection);
        var service = BookingTestBase.CreateCatalogService(
            context, new StubModuleEntitlements(enabledModules));
        var owner = BookingTestBase.CreateOwner();
        var business = BookingTestBase.CreateBusiness(owner.Id);
        await BookingTestBase.SeedAsync(context, owner, business);
        return (context, service, business);
    }

    [Fact]
    public async Task Roster_IsRefused_WhenTheStaffModuleIsWithdrawn()
    {
        using var connection = BookingTestBase.CreateConnection();
        var (context, service, business) = await CreateEnvAsync(connection, "serviceCatalog");
        using var _ = context;

        var result = await service.GetEligibleStaffAsync(business.Id, Array.Empty<Guid>());

        Assert.False(result.Success);
        Assert.Equal("MODULE_DISABLED", result.Error!.Code);
        Assert.Contains("staff", result.Error.Message);
    }

    [Fact]
    public async Task Roster_IsRefused_WhenTheServiceCatalogModuleIsWithdrawn()
    {
        using var connection = BookingTestBase.CreateConnection();
        var (context, service, business) = await CreateEnvAsync(connection, "staff");
        using var _ = context;

        var result = await service.GetEligibleStaffAsync(business.Id, Array.Empty<Guid>());

        Assert.False(result.Success);
        Assert.Equal("MODULE_DISABLED", result.Error!.Code);
        Assert.Contains("serviceCatalog", result.Error.Message);
    }

    [Fact]
    public async Task Roster_IsServed_WhenBothModulesAreEntitled()
    {
        using var connection = BookingTestBase.CreateConnection();
        var (context, service, business) = await CreateEnvAsync(connection, "serviceCatalog", "staff");
        using var _ = context;

        var result = await service.GetEligibleStaffAsync(business.Id, Array.Empty<Guid>());

        Assert.True(result.Success, result.Error?.Message);
        Assert.Empty(result.Data!);
    }

    /// <summary>
    /// An unknown address stays NOT_FOUND, so the storefront can still tell
    /// "wrong address" from "the team was withdrawn".
    /// </summary>
    [Fact]
    public async Task Roster_ForAnUnknownBusiness_IsNotFound_NotModuleDisabled()
    {
        using var connection = BookingTestBase.CreateConnection();
        var (context, service, _) = await CreateEnvAsync(connection, "serviceCatalog", "staff");
        using var __ = context;

        var result = await service.GetEligibleStaffAsync(Guid.NewGuid(), Array.Empty<Guid>());

        Assert.False(result.Success);
        Assert.Equal("NOT_FOUND", result.Error!.Code);
    }
}
