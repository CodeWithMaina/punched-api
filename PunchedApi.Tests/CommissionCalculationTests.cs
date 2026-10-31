using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Tests;

/// <summary>
/// Commission calculation and configuration precedence.
///
/// <para>Covers the rules that make an amount reproducible: the amount is
/// derived only from the frozen arrangement, the most specific configuration
/// wins, and a disabled or inactive arrangement is never used.</para>
/// </summary>
public class CommissionCalculationTests
{
    [Theory]
    [InlineData(10, 1000, 100)]
    [InlineData(12.5, 800, 100)]
    [InlineData(7.25, 333.33, 24.17)]   // 24.166425 rounded half away from zero
    [InlineData(0, 500, 0)]
    public void Percentage_is_applied_to_the_base_and_rounded(
        decimal percentage, decimal baseAmount, decimal expected)
    {
        var amount = CommissionCalculator.Calculate(
            CommissionCalculationType.Percentage, percentage, null, baseAmount, 2);

        Assert.Equal(expected, amount);
    }

    [Theory]
    [InlineData(150, 0, 150)]
    [InlineData(150, 9999, 150)]   // a fixed arrangement ignores the base entirely
    public void Fixed_is_independent_of_the_base(decimal fixedAmount, decimal baseAmount, decimal expected)
    {
        var amount = CommissionCalculator.Calculate(
            CommissionCalculationType.Fixed, null, fixedAmount, baseAmount, 2);

        Assert.Equal(expected, amount);
    }

    [Fact]
    public void Rounding_precision_is_honoured()
    {
        var threeDecimals = CommissionCalculator.Calculate(
            CommissionCalculationType.Percentage, 33.3333m, null, 100m, 3);
        var zeroDecimals = CommissionCalculator.Calculate(
            CommissionCalculationType.Percentage, 33.3333m, null, 100m, 0);

        Assert.Equal(33.333m, threeDecimals);
        Assert.Equal(33m, zeroDecimals);
    }

    [Fact]
    public void Most_specific_arrangement_wins()
    {
        var businessId = Guid.NewGuid();
        var staffId = Guid.NewGuid();
        var serviceId = Guid.NewGuid();

        var config = BusinessOperationsTestBase.EnabledPercentageConfig(businessId, percentage: 5m);
        var set = new CommissionResolutionSet(config, new[]
        {
            Rate(businessId, CommissionScope.Staff, staffId, null, 8m),
            Rate(businessId, CommissionScope.Service, null, serviceId, 11m),
            Rate(businessId, CommissionScope.StaffService, staffId, serviceId, 20m)
        });

        Assert.Equal(20m, set.Resolve(staffId, serviceId)!.Percentage);
        Assert.Equal(CommissionScope.StaffService, set.Resolve(staffId, serviceId)!.Scope);
    }

    [Fact]
    public void Service_beats_staff_and_staff_beats_business_default()
    {
        var businessId = Guid.NewGuid();
        var staffId = Guid.NewGuid();
        var serviceId = Guid.NewGuid();
        var otherServiceId = Guid.NewGuid();

        var config = BusinessOperationsTestBase.EnabledPercentageConfig(businessId, percentage: 5m);
        var set = new CommissionResolutionSet(config, new[]
        {
            Rate(businessId, CommissionScope.Staff, staffId, null, 8m),
            Rate(businessId, CommissionScope.Service, null, serviceId, 11m)
        });

        Assert.Equal(11m, set.Resolve(staffId, serviceId)!.Percentage);
        Assert.Equal(8m, set.Resolve(staffId, otherServiceId)!.Percentage);
        Assert.Equal(5m, set.Resolve(Guid.NewGuid(), otherServiceId)!.Percentage);
        Assert.Equal(CommissionScope.Business, set.Resolve(Guid.NewGuid(), otherServiceId)!.Scope);
    }

    [Fact]
    public void Inactive_override_is_ignored()
    {
        var businessId = Guid.NewGuid();
        var staffId = Guid.NewGuid();
        var config = BusinessOperationsTestBase.EnabledPercentageConfig(businessId, percentage: 5m);

        var inactive = Rate(businessId, CommissionScope.Staff, staffId, null, 50m);
        inactive.IsActive = false;

        var set = new CommissionResolutionSet(config, new[] { inactive });
        Assert.Equal(5m, set.Resolve(staffId, null)!.Percentage);
    }

    [Fact]
    public void Disabled_configuration_resolves_nothing()
    {
        var businessId = Guid.NewGuid();
        var config = BusinessOperationsTestBase.EnabledPercentageConfig(businessId);
        config.IsEnabled = false;

        var set = new CommissionResolutionSet(config, Array.Empty<CommissionRate>());
        Assert.False(set.IsEnabled);
        Assert.Null(set.Resolve(Guid.NewGuid(), Guid.NewGuid()));
    }

    [Fact]
    public void Applied_arrangement_is_frozen_into_the_snapshot()
    {
        var businessId = Guid.NewGuid();
        var staffId = Guid.NewGuid();
        var config = BusinessOperationsTestBase.EnabledPercentageConfig(businessId, percentage: 5m);
        var set = new CommissionResolutionSet(config, new[] { Rate(businessId, CommissionScope.Staff, staffId, null, 8m) });

        var snapshot = set.Resolve(staffId, null)!.SnapshotJson;

        // The snapshot records BOTH the applied override and the business
        // default it overrode, so a later configuration change is provably not
        // what produced a historical amount.
        Assert.Contains("\"appliedPercentage\":8", snapshot);
        Assert.Contains("\"configPercentage\":5", snapshot);
    }

    private static CommissionRate Rate(
        Guid businessId, CommissionScope scope, Guid? staffId, Guid? serviceId, decimal percentage) => new()
    {
        Id = Guid.NewGuid(),
        BusinessId = businessId,
        Scope = scope,
        StaffUserId = staffId,
        ServiceCatalogItemId = serviceId,
        ScopeKey = CommissionRate.BuildScopeKey(scope, staffId, serviceId),
        CalculationType = CommissionCalculationType.Percentage,
        Percentage = percentage,
        IsActive = true,
        UpdatedAt = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow
    };
}
