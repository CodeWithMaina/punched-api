using Microsoft.EntityFrameworkCore;
using Moq;
using PunchedApi.Application.Authorization;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Tests;

/// <summary>
/// Existing-business staff permission onboarding.
///
/// <para>An already-provisioned business must keep exactly the permissions it had:
/// the platform never grants new staff authority silently. The onboarding view
/// reports the gap, the shortcut refuses arbitrary codes, and only an explicit
/// owner action writes the grant.</para>
/// </summary>
public class StaffPermissionOnboardingTests : IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public StaffPermissionOnboardingTests()
    {
        _connection = BookingTestBase.CreateConnection();
        _context = BookingTestBase.CreateContext(_connection);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task ExistingBusiness_GapsAreExplicit_AndOnlyConfirmedGrantsAreApplied()
    {
        var owner = BookingTestBase.CreateOwner("onboarding-owner@test.com");
        var business = BookingTestBase.CreateBusiness(owner.Id, "Legacy Studio");
        await BookingTestBase.SeedAsync(_context, owner, business);

        IBusinessPermissionService permissions = BusinessOperationsTestBase.CreatePermissionService(_context);
        await permissions.EnsureProvisionedAsync(business.Id);

        // Simulate a business provisioned before the new capabilities shipped.
        var roleIds = await _context.BusinessRoles
            .Where(r => r.BusinessId == business.Id)
            .ToDictionaryAsync(r => r.Key, r => r.Id);

        var newCodes = new[] { "appointments.create", "payments.verify" };
        var seededGrants = await _context.BusinessRolePermissions
            .Where(p => roleIds.Values.Contains(p.BusinessRoleId) && newCodes.Contains(p.PermissionCode))
            .ToListAsync();
        Assert.NotEmpty(seededGrants);

        _context.BusinessRolePermissions.RemoveRange(seededGrants);
        await _context.SaveChangesAsync();
        permissions.Invalidate(business.Id);

        var scope = new Mock<IBusinessScopeResolver>();
        scope.Setup(s => s.GetOwnedBusinessIdAsync(owner.Id)).ReturnsAsync(business.Id);
        scope.Setup(s => s.GetMemberBusinessIdAsync(owner.Id)).ReturnsAsync(business.Id);

        var onboarding = new StaffPermissionOnboardingService(
            _context,
            scope.Object,
            permissions,
            TestHelpers.CreateLogger<StaffPermissionOnboardingService>());

        var before = await onboarding.GetAsync(owner.Id);
        Assert.True(before.Success, before.Error?.Message);
        Assert.True(before.Data!.HasGaps);
        Assert.Contains(before.Data.Recommendations, r => r.PermissionCode == "appointments.create");
        Assert.Contains(before.Data.Recommendations, r => r.PermissionCode == "payments.verify");

        // A capability outside the closed list cannot be smuggled through this shortcut.
        var refused = await onboarding.ApplyAsync(owner.Id, new ApplyStaffPermissionOnboardingRequest
        {
            Grants =
            [
                new StaffPermissionGrantRequest
                {
                    RoleId = roleIds[PermissionCatalog.DefaultStaffRoleKey],
                    PermissionCodes = ["roles.manage"]
                }
            ]
        });
        Assert.False(refused.Success);
        Assert.Equal("UNKNOWN_PERMISSION", refused.Error?.Code);

        var grants = before.Data.Recommendations
            .GroupBy(r => r.RoleId)
            .Select(g => new StaffPermissionGrantRequest
            {
                RoleId = g.Key,
                PermissionCodes = g.Select(r => r.PermissionCode).ToList()
            })
            .ToList();

        var after = await onboarding.ApplyAsync(owner.Id, new ApplyStaffPermissionOnboardingRequest
        {
            Grants = grants
        });

        Assert.True(after.Success, after.Error?.Message);
        Assert.False(after.Data!.HasGaps);

        var serviceProfessional = roleIds[PermissionCatalog.DefaultStaffRoleKey];
        Assert.True(await _context.BusinessRolePermissions.AnyAsync(p =>
            p.BusinessRoleId == serviceProfessional && p.PermissionCode == "appointments.create"));
        Assert.True(await _context.BusinessRolePermissions.AnyAsync(p =>
            p.BusinessRoleId == roleIds["receptionist"] && p.PermissionCode == "payments.verify"));
    }
}
