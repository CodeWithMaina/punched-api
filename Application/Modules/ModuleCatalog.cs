namespace PunchedApi.Application.Modules;

/// <summary>
/// Packaging tier of a module. Informational (drives pricing/plan grouping);
/// the DB <c>modules</c> table remains the join target while this catalog is
/// the runtime metadata + permission authority.
/// </summary>
public enum ModuleVisibility { Core, Standard, Premium, Enterprise, Internal }

/// <summary>
/// A module's static definition: identity, dependencies, the roles that can
/// see it, the CUSTOMER capability it powers (if any), and the fine-grained
/// permissions it grants per role.
/// </summary>
/// <param name="CustomerCapability">
/// The customer-facing capability this module powers
/// (see <see cref="CustomerCapabilityCatalog"/>), or <c>null</c> when the
/// module is business/internal ONLY.
///
/// <para>This is deliberately a SECOND, explicit axis alongside
/// <see cref="RequiredRoles"/> — a module may be reachable by a Customer role
/// (e.g. "stamps" grants <c>stamps.view</c> to Customers) without being a
/// customer-facing capability in the customer app (the customer's stamp
/// surface is the "loyalty" capability, rendered on the loyalty card). Stating
/// it per module is what stops an internal module leaking into the customer
/// experience merely because a role happens to be listed.</para>
/// </param>
public sealed record ModuleDefinition(
    string Key,
    string Name,
    string Description,
    string Version,
    ModuleVisibility Visibility,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> RequiredRoles,
    string? CustomerCapability,
    IReadOnlyList<PermissionDefinition> Permissions
);

/// <summary>
/// A single permission code and the roles it is granted to.
/// </summary>
public sealed record PermissionDefinition(string Code, IReadOnlyList<string> Roles);

/// <summary>
/// Runtime module catalog. The <c>Key</c> values MUST match
/// <c>PunchedApi.Infrastructure.SeedData.ModuleSeedData</c> exactly — the DB
/// <c>modules</c> table is the join/entitlement target and this catalog is the
/// metadata + permission authority. All keys are lowercase and immutable once
/// released.
/// </summary>
public static class ModuleCatalog
{
    public static readonly IReadOnlyList<ModuleDefinition> Modules = new[]
    {
        // ── Core (always available to every business) ───────────
        new ModuleDefinition(
            Key: "customers", Name: "Customers",
            Description: "Customer management and profiles",
            Version: "1.0.0", Visibility: ModuleVisibility.Core,
            Dependencies: Array.Empty<string>(),
            RequiredRoles: new[] { "Business", "Staff" },
            // Business-internal: customer records are managed BY the business.
            CustomerCapability: null,
            Permissions: new[]
            {
                new PermissionDefinition("customers.view",   new[] { "Business", "Staff" }),
                new PermissionDefinition("customers.manage", new[] { "Business" }),
            }),
        new ModuleDefinition(
            Key: "staff", Name: "Staff",
            Description: "Staff management, shifts and invitations",
            Version: "1.0.0", Visibility: ModuleVisibility.Core,
            Dependencies: Array.Empty<string>(),
            RequiredRoles: new[] { "Business", "Staff" },
            // Business-internal: rota/invitations are owner/staff surfaces.
            CustomerCapability: null,
            Permissions: new[]
            {
                new PermissionDefinition("staff.view",   new[] { "Business", "Staff" }),
                new PermissionDefinition("staff.manage", new[] { "Business" }),
            }),
        new ModuleDefinition(
            Key: "settings", Name: "Settings",
            Description: "Business settings and profile",
            Version: "1.0.0", Visibility: ModuleVisibility.Core,
            Dependencies: Array.Empty<string>(),
            RequiredRoles: new[] { "Business" },
            // Business-internal. The customer's own Settings tab is a core
            // customer route, never this module.
            CustomerCapability: null,
            Permissions: new[]
            {
                new PermissionDefinition("settings.view",   new[] { "Business" }),
                new PermissionDefinition("settings.manage", new[] { "Business" }),
            }),

        // ── Standard ────────────────────────────────────────────
        new ModuleDefinition(
            Key: "appointments", Name: "Appointments",
            Description: "Booking management",
            Version: "1.0.0", Visibility: ModuleVisibility.Standard,
            Dependencies: new[] { "customers", "staff" },
            RequiredRoles: new[] { "Business", "Staff", "Customer" },
            CustomerCapability: "appointments",
            Permissions: new[]
            {
                new PermissionDefinition("appointments.view",   new[] { "Business", "Staff", "Customer" }),
                new PermissionDefinition("appointments.manage", new[] { "Business" }),
                new PermissionDefinition("appointments.create", new[] { "Customer" }),
            }),
        new ModuleDefinition(
            Key: "stamps", Name: "Stamps",
            Description: "Digital stamp cards",
            Version: "1.0.0", Visibility: ModuleVisibility.Standard,
            Dependencies: new[] { "customers" },
            RequiredRoles: new[] { "Business", "Staff", "Customer" },
            // The customer's stamp surface IS the loyalty card: QrController is
            // [Authorize(Roles = "Customer")] + [RequireModule("stamps")], and
            // the card renders the stamps. There is deliberately no separate
            // "Stamps" customer capability — it maps on to "loyalty".
            CustomerCapability: "loyalty",
            Permissions: new[]
            {
                                new PermissionDefinition("stamps.view",  new[] { "Business", "Staff", "Customer" }),
                new PermissionDefinition("stamps.award", new[] { "Business", "Staff" }),
                new PermissionDefinition("stamps.adjust", new[] { "Business" }),
            }),
        new ModuleDefinition(
            Key: "notifications", Name: "Notifications",
            Description: "Push notifications",
            Version: "1.0.0", Visibility: ModuleVisibility.Standard,
            Dependencies: new[] { "customers", "staff" },
            RequiredRoles: new[] { "Business", "Staff", "Customer" },
            CustomerCapability: "notifications",
            Permissions: new[]
            {
                new PermissionDefinition("notifications.view",   new[] { "Business", "Staff", "Customer" }),
                new PermissionDefinition("notifications.manage", new[] { "Business" }),
            }),
        new ModuleDefinition(
            Key: "serviceCatalog", Name: "Service Catalog",
            Description: "Bookable services the business offers",
            Version: "1.0.0", Visibility: ModuleVisibility.Standard,
            Dependencies: Array.Empty<string>(),
            RequiredRoles: new[] { "Business", "Customer" },
            CustomerCapability: "services",
            Permissions: new[]
            {
                new PermissionDefinition("serviceCatalog.view",   new[] { "Business", "Customer" }),
                new PermissionDefinition("serviceCatalog.manage", new[] { "Business" }),
            }),
        new ModuleDefinition(
            Key: "attendance", Name: "Attendance",
            Description: "Staff clock-in and clock-out with business QR codes",
            Version: "1.0.0", Visibility: ModuleVisibility.Standard,
            Dependencies: new[] { "staff" },
            RequiredRoles: new[] { "Business", "Staff" },
            // Business-internal: a business may have Attendance enabled while
            // its customers have no attendance surface at all.
            CustomerCapability: null,
            Permissions: new[]
            {
                new PermissionDefinition("attendance.view",   new[] { "Business", "Staff" }),
                new PermissionDefinition("attendance.clock",  new[] { "Business", "Staff" }),
                new PermissionDefinition("attendance.manage", new[] { "Business" }),
            }),


        new ModuleDefinition(
            Key: "payments", Name: "Payments",
            Description: "Direct-to-business payment collection (cash + M-PESA)",
            Version: "1.0.0", Visibility: ModuleVisibility.Standard,
            // Payments is a customer-facing capability: the customer sees
            // payment requests for this business.
            Dependencies: new[] { "appointments" },
            RequiredRoles: new[] { "Business", "Staff", "Customer" },
            CustomerCapability: "payments",
            Permissions: new[]
            {
                new PermissionDefinition("payments.view",        new[] { "Business", "Staff", "Customer" }),
                new PermissionDefinition("payments.create",      new[] { "Business", "Staff", "Customer" }),
                new PermissionDefinition("payments.confirm_cash", new[] { "Business", "Staff" }),
                new PermissionDefinition("payments.configure",   new[] { "Business" }),
                new PermissionDefinition("payments.reverse",     new[] { "Business" }),
            }),

        // ── Premium ─────────────────────────────────────────────
        new ModuleDefinition(
            Key: "loyalty", Name: "Loyalty Programs",
            Description: "Loyalty program management",
            Version: "1.0.0", Visibility: ModuleVisibility.Premium,
            Dependencies: new[] { "customers", "stamps" },
            RequiredRoles: new[] { "Business", "Customer" },
            CustomerCapability: "loyalty",
            Permissions: new[]
            {
                new PermissionDefinition("loyalty.view",   new[] { "Business", "Customer" }),
                new PermissionDefinition("loyalty.manage", new[] { "Business" }),
                new PermissionDefinition("loyalty.stamp",  new[] { "Business", "Staff" }),
                new PermissionDefinition("loyalty.redeem", new[] { "Business", "Staff" }),
            }),
        new ModuleDefinition(
            Key: "rewards", Name: "Rewards",
            Description: "Reward catalog",
            Version: "1.0.0", Visibility: ModuleVisibility.Premium,
            Dependencies: new[] { "loyalty", "stamps" },
            RequiredRoles: new[] { "Business", "Customer" },
            CustomerCapability: "rewards",
            Permissions: new[]
            {
                                new PermissionDefinition("rewards.view",   new[] { "Business", "Customer" }),
                new PermissionDefinition("rewards.manage", new[] { "Business" }),
                new PermissionDefinition("redemptions.fulfill", new[] { "Business", "Staff" }),
            }),
        new ModuleDefinition(
            Key: "analytics", Name: "Analytics",
            Description: "Business analytics",
            Version: "1.0.0", Visibility: ModuleVisibility.Premium,
            // Must stay in sync with ModuleSeedData.DependenciesJson
            // (["customers","stamps","loyalty"]) — asserted by
            // ModuleCatalogSyncTests.
            Dependencies: new[] { "customers", "stamps", "loyalty" },
            RequiredRoles: new[] { "Business" },
            // Business-internal: analytics is the owner's reporting surface.
            CustomerCapability: null,
            Permissions: new[]
            {
                new PermissionDefinition("analytics.view", new[] { "Business" }),
            }),
        new ModuleDefinition(
            Key: "programs", Name: "Programs",
            Description: "Custom program builder",
            Version: "1.0.0", Visibility: ModuleVisibility.Premium,
            Dependencies: new[] { "loyalty" },
            RequiredRoles: new[] { "Business" },
            // Business-internal: program authoring is an owner tool. Customers
            // experience the resulting program through "loyalty"/"rewards".
            CustomerCapability: null,
            Permissions: new[]
            {
                new PermissionDefinition("programs.view",   new[] { "Business" }),
                new PermissionDefinition("programs.manage", new[] { "Business" }),
            }),
        new ModuleDefinition(
            Key: "referral", Name: "Referrals",
            Description: "Customer referral program",
            Version: "1.0.0", Visibility: ModuleVisibility.Premium,
            Dependencies: new[] { "loyalty", "stamps" },
            RequiredRoles: new[] { "Business", "Customer" },
            CustomerCapability: "referrals",
            Permissions: new[]
            {
                new PermissionDefinition("referral.view",   new[] { "Business", "Customer" }),
                new PermissionDefinition("referral.manage", new[] { "Business" }),
            }),
        new ModuleDefinition(
            Key: "customCardDesign", Name: "Custom Card Design",
            Description: "Business-specific HTML card designs for loyalty stamp cards",
            Version: "1.0.0", Visibility: ModuleVisibility.Premium,
            // Must stay in sync with ModuleSeedData.DependenciesJson
            // (["loyalty"]) — asserted by ModuleCatalogSyncTests.
            // Loyalty deliberately does NOT depend on this module: the default
            // card design ships with loyalty, custom designs are an enhancement.
            Dependencies: new[] { "loyalty" },
            RequiredRoles: new[] { "Business" },
            // Business-internal: authoring HTML card templates is an owner tool.
            CustomerCapability: null,
            Permissions: new[]
            {
                new PermissionDefinition("cardDesigns.view",   new[] { "Business" }),
                new PermissionDefinition("cardDesigns.select", new[] { "Business" }),
            }),
    };

    /// <summary>Finds a module definition by key (case-insensitive).</summary>
    public static ModuleDefinition? Find(string key) =>
        Modules.FirstOrDefault(m => m.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Transitive dependency closure of the given module keys, per the catalog.
    /// A module's dependencies are treated as available for access purposes even
    /// when not separately enabled (plan §14.1).
    /// </summary>
    public static HashSet<string> CloseDependencies(IEnumerable<string> moduleKeys)
    {
        var closed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(moduleKeys);

        while (queue.Count > 0)
        {
            var key = queue.Dequeue();
            if (!closed.Add(key)) continue;

            var definition = Find(key);
            if (definition == null) continue;

            foreach (var dependency in definition.Dependencies)
            {
                if (!closed.Contains(dependency))
                    queue.Enqueue(dependency);
            }
        }

        return closed;
    }
}
