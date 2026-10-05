namespace PunchedApi.Application.Modules;

/// <summary>
/// The capabilities a business can expose to its CUSTOMERS — the customer-facing
/// projection of <see cref="ModuleCatalog.Modules"/>.
///
/// <para><b>Why this exists.</b> A business module and a customer capability are
/// different things. "Attendance" is a business module with no customer surface;
/// "Appointments" is a business module whose customer surface is booking. The
/// customer app must only ever ask "what can this customer do with THIS
/// business?", never "what features does Punched have?" — so the mapping lives
/// here, in ONE place, and is derived from the SAME effective entitlements the
/// owner shell and <c>[RequireModule]</c> use. There is no second subscription
/// or module system.</para>
///
/// <para><b>Sources of access.</b> This class deliberately knows nothing about
/// HOW access was granted. Plan grants, purchases, trials, admin overrides,
/// promos and enterprise deals all land in
/// <c>IModuleEntitlementService.GetEffectiveModuleKeysAsync</c>; the caller
/// passes the result in and this class only maps modules → customer
/// capabilities. New entitlement mechanisms therefore need no change here.</para>
///
/// <para><b>Dependencies.</b> Module dependencies are resolved with the same
/// <see cref="ModuleCatalog.CloseDependencies"/> closure used for access checks
/// everywhere else, so an entitled module never exposes a capability whose
/// supporting module is missing.</para>
/// </summary>
public static class CustomerCapabilityCatalog
{
    // ── Capability keys ─────────────────────────────────────────────
    // Stable, lowercase, immutable once released. These are the exact keys
    // sent to the customer app and mirrored by registry/customerCapabilities.ts.

    /// <summary>Browse the business's bookable service catalogue.</summary>
    public const string Services = "services";

    /// <summary>Book and manage appointments.</summary>
    public const string Appointments = "appointments";

    /// <summary>Stamp cards / loyalty membership.</summary>
    public const string Loyalty = "loyalty";

    /// <summary>Redeem loyalty rewards.</summary>
    public const string Rewards = "rewards";

    /// <summary>Referrals: invite others to this business.</summary>
    public const string Referrals = "referrals";

    /// <summary>Pay a business (cash + M-PESA requests).</summary>
    public const string Payments = "payments";

    /// <summary>Read/receive business notifications.</summary>
    public const string Notifications = "notifications";

    /// <summary>
    /// Leave a review. Core rather than module-gated: reviews are written
    /// against a completed appointment, so this capability is derived from
    /// <see cref="Appointments"/> rather than from a catalog module
    /// (<c>ReviewController</c> carries no <c>[RequireModule]</c>).
    /// </summary>
    public const string Reviews = "reviews";

    /// <summary>
    /// Every customer capability, in the order the customer dashboard should
    /// consider them. Ordering is presentation-only and intentionally stable.
    /// </summary>
    public static readonly IReadOnlyList<string> AllKeys = new[]
    {
        Services,
        Appointments,
        Loyalty,
        Rewards,
        Reviews,
        Referrals,
        Payments,
        Notifications,
    };

    /// <summary>Capabilities the customer app treats as the "home" core set.</summary>
    public static readonly IReadOnlySet<string> CustomerSurfaceKeys =
        new HashSet<string>(AllKeys, StringComparer.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="key"/> names a known customer capability.</summary>
    public static bool IsKnownCapability(string? key) =>
        key != null && CustomerSurfaceKeys.Contains(key);

    /// <summary>
    /// Does this module expose anything to customers at all?
    /// <c>false</c> for every business/internal module (attendance, analytics,
    /// staff, settings, customers, customCardDesign, programs, stamps).
    /// </summary>
    public static bool IsCustomerFacing(string moduleKey) =>
        ModuleCatalog.Find(moduleKey)?.CustomerCapability != null;

    /// <summary>Business modules that power the given customer capability.</summary>
    public static IReadOnlyList<string> ModulesFor(string capabilityKey) =>
        ModuleCatalog.Modules
            .Where(m => string.Equals(m.CustomerCapability, capabilityKey, StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Key)
            .ToList();

    /// <summary>
    /// Resolve the capabilities a business currently exposes to customers.
    ///
    /// <para><paramref name="effectiveModuleKeys"/> is the business's effective
    /// entitlement set (any source). <paramref name="hasActiveLoyaltyProgram"/>
    /// and <paramref name="hasActiveReferralProgram"/> are the two cases where a
    /// granted module is not by itself customer-usable: the business must have
    /// actually configured something a customer can join or refer into.</para>
    ///
    /// <para>The result ALWAYS contains every key in <see cref="AllKeys"/>
    /// (false when unavailable), so the customer app can consume a fixed shape
    /// and never has to distinguish "absent" from "false".</para>
    /// </summary>
    public static IReadOnlyDictionary<string, bool> Resolve(
        IEnumerable<string> effectiveModuleKeys,
        bool hasActiveLoyaltyProgram,
        bool hasActiveReferralProgram)
    {
        // Same access semantics as BusinessContext/[RequireModule]: a module's
        // dependencies are available for access purposes even when not
        // separately enabled.
        var closed = ModuleCatalog.CloseDependencies(effectiveModuleKeys);

        bool has(string moduleKey) => closed.Contains(moduleKey);

        bool enabled(string capabilityKey) => capabilityKey switch
        {
            Services => has("serviceCatalog"),
            Appointments => has("appointments"),
            Payments => has("payments"),
            Notifications => has("notifications"),

            // Loyalty is only a customer capability when the business has an
            // ACTIVE program to belong to; the module alone is an empty shell.
            Loyalty => has("loyalty") && hasActiveLoyaltyProgram,

            // Rewards additionally needs its own module — otherwise there is
            // nothing in the catalog to redeem.
            Rewards => has("rewards") && hasActiveLoyaltyProgram,

            // A referral link is useless without a live program.
            Referrals => has("referral") && hasActiveReferralProgram,

            // Reviews follow the appointment they are written against.
            Reviews => has("appointments"),

            _ => false,
        };

        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in AllKeys) result[key] = enabled(key);
        return result;
    }

    /// <summary>
    /// Convenience: does this business expose the given customer capability?
    /// Unknown keys are never exposed.
    /// </summary>
    public static bool IsEnabled(
        string capabilityKey,
        IEnumerable<string> effectiveModuleKeys,
        bool hasActiveLoyaltyProgram,
        bool hasActiveReferralProgram) =>
        IsKnownCapability(capabilityKey) &&
        Resolve(effectiveModuleKeys, hasActiveLoyaltyProgram, hasActiveReferralProgram)[capabilityKey];

    /// <summary>
    /// Module-level gate used by <c>IBusinessContext</c> for CUSTOMER callers:
    /// the module must exist, expose something to customers, and be part of the
    /// business's effective (dependency-closed) entitlements.
    /// </summary>
    public static bool CustomerMayUseModule(
        string moduleKey,
        IEnumerable<string> effectiveModuleKeys)
    {
        if (!IsCustomerFacing(moduleKey)) return false;
        return ModuleCatalog.CloseDependencies(effectiveModuleKeys).Contains(moduleKey);
    }
}
