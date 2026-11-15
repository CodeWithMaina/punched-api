namespace PunchedApi.Infrastructure.SeedData;

/// <summary>
/// Static definition of plan → module assignments (the
/// <c>plan_modules</c> table). Keys reference <see cref="ModuleSeedData"/>
/// and <see cref="SubscriptionPlanSeedData"/> entries.
/// </summary>
public static class PlanModuleSeedData
{
    public static List<(string PlanKey, string ModuleKey)> GetPlanModules() => new()
    {
        // Reviews / gallery / storefront are granted on EVERY tier by default
        // (so no existing business loses access) but remain individually
        // switchable per plan — admins toggle them off via
        // AdminSubscriptionTiersController.SetModulesAsync, or per business
        // via AdminModulesController overrides.

        // Starter
        ("starter", "customers"), ("starter", "staff"), ("starter", "settings"),
        ("starter", "reviews"), ("starter", "gallery"), ("starter", "storefront"),

        // Growth
        ("growth", "customers"), ("growth", "staff"), ("growth", "settings"),
        ("growth", "appointments"), ("growth", "stamps"), ("growth", "notifications"),
        ("growth", "serviceCatalog"), ("growth", "attendance"), ("growth", "payments"),
        ("growth", "reviews"), ("growth", "gallery"), ("growth", "storefront"),
        // Commissions is an operational/financial capability: granted from
        // Growth up (never Starter), and its "appointments" dependency is
        // granted explicitly in the same plan.
        ("growth", "commissions"),

        // Finances: the ledger/cashbook/reconciliation capability, granted from Growth up.
        // Its "payments" dependency is granted explicitly in every plan that gets it.
        //
        // "Enabled by default" means exactly this: no override is needed, so a business whose
        // plan grants Finance sees it from its first login. It is deliberately NOT granted on
        // the entry tier, because Finance's ledger is fed by appointment payments — granting it
        // would drag the appointments module (and a booking funnel the entry tier does not
        // sell) into that plan. The owner switch in Settings turns it off per business without
        // deleting a record; turning it back on is instant wherever the plan grants it.
        ("growth", "financials"),

        // Pro
        ("pro", "customers"), ("pro", "staff"), ("pro", "settings"),
        ("pro", "appointments"), ("pro", "stamps"), ("pro", "notifications"),
        ("pro", "loyalty"), ("pro", "analytics"), ("pro", "referral"), ("pro", "serviceCatalog"),
        ("pro", "attendance"), ("pro", "payments"), ("pro", "commissions"), ("pro", "financials"),
        ("pro", "reviews"), ("pro", "gallery"), ("pro", "storefront"),

        // Enterprise — the only tier that includes the optional Custom Card
        // Design enhancement. Every tier with "loyalty" keeps the DEFAULT card;
        // only enterprise may use business-specific designs.
        ("enterprise", "customers"), ("enterprise", "staff"), ("enterprise", "settings"),
        ("enterprise", "appointments"), ("enterprise", "stamps"), ("enterprise", "notifications"),
        ("enterprise", "loyalty"), ("enterprise", "rewards"), ("enterprise", "analytics"), ("enterprise", "programs"),
        ("enterprise", "referral"), ("enterprise", "serviceCatalog"), ("enterprise", "attendance"),
        ("enterprise", "customCardDesign"), ("enterprise", "payments"), ("enterprise", "commissions"),
        ("enterprise", "financials"),
        ("enterprise", "reviews"), ("enterprise", "gallery"), ("enterprise", "storefront")
    };
}