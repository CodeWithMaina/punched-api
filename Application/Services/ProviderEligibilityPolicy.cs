using PunchedApi.Domain.Entities;

namespace PunchedApi.Application.Services;

/// <summary>
/// <b>One policy for "who may provide this service", and whether the client may name them.</b>
///
/// <para>Three separate questions used to be answered in three places — the booking read
/// context, the eligibility service and the availability engine's owner check. They drifted:
/// a business whose Staff <i>module</i> was switched off but which still had a rostered team
/// was collapsed to its owner, so a fully-booked owner made the whole business look booked
/// while nine eligible staff sat idle.</para>
///
/// <para>The distinction this type exists to keep is:</para>
/// <list type="bullet">
///   <item><description><b>Entitlement</b> — is the Staff <i>module</i> switched on? It governs
///   staff-management screens and whether a customer/employee may hand-pick a provider.</description></item>
///   <item><description><b>Capacity</b> — the business's operating mode plus its existing roster,
///   shifts, breaks and assignments. It governs who can actually take a booking.</description></item>
/// </list>
/// <para>Entitlement never adds or removes capacity. It only decides whether the provider is
/// chosen explicitly by a person or automatically by the engine.</para>
/// </summary>
public static class ProviderEligibilityPolicy
{
    /// <summary>Genuine solo businesses (owner is the only scheduling resource).</summary>
    public static bool IsSolo(string? operatingMode) =>
        string.Equals(operatingMode, BusinessOperatingModes.Solo, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The effective provider model, from the three facts that actually decide it.
    ///
    /// <para>The returned <b>name</b> is a UI contract — it tells the product whether to show
    /// a provider picker — while <see cref="AllowsExplicitProviderSelection"/> and the
    /// eligible-provider set decide behaviour. That split is what lets a business keep its
    /// team's capacity with no picker on screen.</para>
    /// </summary>
    /// <param name="operatingMode">The persisted <see cref="Domain.Entities.Business.OperatingMode"/>.</param>
    /// <param name="staffModuleEnabled">Is the Staff module entitled for this business?</param>
    /// <param name="hasBookableStaff">Does the business have active, bookable staff members?</param>
    public static string ResolveModel(string? operatingMode, bool staffModuleEnabled, bool hasBookableStaff)
    {
        if (IsSolo(operatingMode)) return EffectiveProviderModel.Solo;

        // Nobody to allocate to: the owner stands in, exactly as before the staff layer existed.
        if (!hasBookableStaff) return EffectiveProviderModel.OwnerOnly;

        // A team exists but its management surfaces are switched off: the engine picks the
        // provider, the customer never does.
        return staffModuleEnabled
            ? EffectiveProviderModel.CategoryBased
            : EffectiveProviderModel.Automatic;
    }

    /// <summary>
    /// May a request name a specific provider?
    ///
    /// <para>Only when explicit selection exists as a product surface — i.e. the category-based
    /// team flow. In every other model the provider is resolved by the engine, so a
    /// client-supplied staff id is not a choice the system honours. This is what stops a
    /// hidden or hand-edited request from choosing staff while the module is off, and from
    /// reaching rostered staff in a solo business.</para>
    /// </summary>
    public static bool AllowsExplicitProviderSelection(string? model) =>
        string.Equals(model, EffectiveProviderModel.CategoryBased, StringComparison.Ordinal);

    /// <summary>
    /// Does the business have exactly one scheduling resource (the owner)?
    /// True for solo and owner-fallback models; false when a roster takes part.
    /// </summary>
    public static bool CollapsesToOwner(string? model) =>
        string.Equals(model, EffectiveProviderModel.Solo, StringComparison.Ordinal) ||
        string.Equals(model, EffectiveProviderModel.OwnerOnly, StringComparison.Ordinal);

    /// <summary>
    /// Does one provider cover one service?
    ///
    /// <para>Categorised services are covered by providers assigned that category. An
    /// <i>uncategorised</i> service (legacy catalogue) is covered by providers explicitly
    /// assigned it; and when no provider in the business has been assigned it at all, it is
    /// open to every provider so a service can never become silently unbookable during a
    /// catalogue migration.</para>
    /// </summary>
    /// <param name="categoryId">The service's category, or null when uncategorised.</param>
    /// <param name="providerHasCategory">Does this provider carry the service's category?</param>
    /// <param name="providerHasExplicitAssignment">Is this provider explicitly assigned the service?</param>
    /// <param name="serviceHasAnyExplicitAssignment">Is any provider assigned the service?</param>
    public static bool ProviderCoversService(
        Guid? categoryId,
        bool providerHasCategory,
        bool providerHasExplicitAssignment,
        bool serviceHasAnyExplicitAssignment)
    {
        if (categoryId is not null) return providerHasCategory;
        if (providerHasExplicitAssignment) return true;
        return !serviceHasAnyExplicitAssignment;
    }
}
