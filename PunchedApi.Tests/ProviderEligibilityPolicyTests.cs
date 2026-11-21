using PunchedApi.Application.Services;

namespace PunchedApi.Tests;

/// <summary>
/// The single provider-eligibility policy.
///
/// <para>These are the rules the storefront, internal booking, appointment creation and
/// calendar all resolve through. They are asserted directly here so a future edit to the
/// policy cannot silently change what "the Staff module is off" means.</para>
/// </summary>
public class ProviderEligibilityPolicyTests
{
    [Fact]
    public void Solo_IsSoloWhateverTheModuleSays()
    {
        // Scenario 4/5: solo scheduling never depends on the Staff entitlement.
        Assert.Equal(
            EffectiveProviderModel.Solo,
            ProviderEligibilityPolicy.ResolveModel("solo", staffModuleEnabled: true, hasBookableStaff: true));
        Assert.Equal(
            EffectiveProviderModel.Solo,
            ProviderEligibilityPolicy.ResolveModel("solo", staffModuleEnabled: false, hasBookableStaff: true));
    }

    [Fact]
    public void MultiStaff_NoTeam_FallsBackToTheOwner()
    {
        Assert.Equal(
            EffectiveProviderModel.OwnerOnly,
            ProviderEligibilityPolicy.ResolveModel("multi_staff", staffModuleEnabled: true, hasBookableStaff: false));
        Assert.Equal(
            EffectiveProviderModel.OwnerOnly,
            ProviderEligibilityPolicy.ResolveModel("multi_staff", staffModuleEnabled: false, hasBookableStaff: false));
    }

    [Fact]
    public void MultiStaff_WithTeam_IsCategoryBasedOnlyWhenTheModuleIsOn()
    {
        Assert.Equal(
            EffectiveProviderModel.CategoryBased,
            ProviderEligibilityPolicy.ResolveModel("multi_staff", staffModuleEnabled: true, hasBookableStaff: true));

        // Scenario 2: the module is off, but the team still provides capacity.
        Assert.Equal(
            EffectiveProviderModel.Automatic,
            ProviderEligibilityPolicy.ResolveModel("multi_staff", staffModuleEnabled: false, hasBookableStaff: true));
    }

    [Fact]
    public void OnlyCategoryBasedAllowsAPersonToChooseTheProvider()
    {
        // Scenario 1: explicit selection exists with the module on.
        Assert.True(ProviderEligibilityPolicy.AllowsExplicitProviderSelection(EffectiveProviderModel.CategoryBased));

        // Scenario 2: no picker, so no honoured selection — however the request is crafted.
        Assert.False(ProviderEligibilityPolicy.AllowsExplicitProviderSelection(EffectiveProviderModel.Automatic));
        Assert.False(ProviderEligibilityPolicy.AllowsExplicitProviderSelection(EffectiveProviderModel.OwnerOnly));
        Assert.False(ProviderEligibilityPolicy.AllowsExplicitProviderSelection(EffectiveProviderModel.Solo));
    }

    [Fact]
    public void OnlySoloAndOwnerFallbackCollapseToTheOwner()
    {
        Assert.True(ProviderEligibilityPolicy.CollapsesToOwner(EffectiveProviderModel.Solo));
        Assert.True(ProviderEligibilityPolicy.CollapsesToOwner(EffectiveProviderModel.OwnerOnly));

        // A switched-off module must never turn a roster into "the owner only".
        Assert.False(ProviderEligibilityPolicy.CollapsesToOwner(EffectiveProviderModel.Automatic));
        Assert.False(ProviderEligibilityPolicy.CollapsesToOwner(EffectiveProviderModel.CategoryBased));
    }

    [Fact]
    public void CategorisedService_NeedsTheCategory()
    {
        var category = Guid.NewGuid();

        Assert.True(ProviderEligibilityPolicy.ProviderCoversService(
            category, providerHasCategory: true, providerHasExplicitAssignment: false,
            serviceHasAnyExplicitAssignment: false));

        Assert.False(ProviderEligibilityPolicy.ProviderCoversService(
            category, providerHasCategory: false, providerHasExplicitAssignment: true,
            serviceHasAnyExplicitAssignment: true));
    }

    [Fact]
    public void UncategorisedService_HonoursAnExplicitAssignment()
    {
        Assert.True(ProviderEligibilityPolicy.ProviderCoversService(
            categoryId: null, providerHasCategory: false, providerHasExplicitAssignment: true,
            serviceHasAnyExplicitAssignment: true));

        Assert.False(ProviderEligibilityPolicy.ProviderCoversService(
            categoryId: null, providerHasCategory: false, providerHasExplicitAssignment: false,
            serviceHasAnyExplicitAssignment: true));
    }

    [Fact]
    public void UncategorisedService_WithNoAssignmentAnywhere_StaysOpen()
    {
        // Scenario 11's counterpart: an unconfigured legacy service must not become
        // silently unbookable.
        Assert.True(ProviderEligibilityPolicy.ProviderCoversService(
            categoryId: null, providerHasCategory: false, providerHasExplicitAssignment: false,
            serviceHasAnyExplicitAssignment: false));
    }
}
