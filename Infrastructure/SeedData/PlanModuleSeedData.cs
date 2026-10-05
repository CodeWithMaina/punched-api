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
        // Starter
        ("starter", "customers"), ("starter", "staff"), ("starter", "settings"),

        // Growth
                ("growth", "customers"), ("growth", "staff"), ("growth", "settings"),
        ("growth", "appointments"), ("growth", "stamps"), ("growth", "notifications"),
        ("growth", "serviceCatalog"), ("growth", "attendance"), ("growth", "payments"),

        // Pro
        ("pro", "customers"), ("pro", "staff"), ("pro", "settings"),
        ("pro", "appointments"), ("pro", "stamps"), ("pro", "notifications"),
        ("pro", "loyalty"), ("pro", "analytics"), ("pro", "referral"), ("pro", "serviceCatalog"),
        ("pro", "attendance"), ("pro", "payments"),

        // Enterprise — the only tier that includes the optional Custom Card
        // Design enhancement. Every tier with "loyalty" keeps the DEFAULT card;
        // only enterprise may use business-specific designs.
        ("enterprise", "customers"), ("enterprise", "staff"), ("enterprise", "settings"),
        ("enterprise", "appointments"), ("enterprise", "stamps"), ("enterprise", "notifications"),
        ("enterprise", "loyalty"), ("enterprise", "rewards"), ("enterprise", "analytics"), ("enterprise", "programs"),
        ("enterprise", "referral"), ("enterprise", "serviceCatalog"), ("enterprise", "attendance"),
        ("enterprise", "customCardDesign"), ("enterprise", "payments")
    };
}