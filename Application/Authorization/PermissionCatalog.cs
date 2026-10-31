using System.Text;
using PunchedApi.Application.Modules;

namespace PunchedApi.Application.Authorization;

/// <summary>One entry of the system permission catalogue.</summary>
public sealed record CatalogPermission(string Code, string Name, string Description, string ModuleKey);

/// <summary>A platform-provided role template seeded into every business.</summary>
public sealed record RoleTemplate(
    string Key,
    string Name,
    string Description,
    bool IsOwnerRole,
    int SortOrder,
    IReadOnlyList<string> Permissions);

/// <summary>
/// <b>The authoritative permission catalogue.</b>
///
/// <para>Permissions are derived from <see cref="ModuleCatalog"/> — the same
/// catalog that already owns module identity, dependencies and entitlements —
/// so a permission can never exist without a module to gate it. The catalogue
/// is code-owned and mirrored into the <c>permissions</c> table at startup
/// (idempotent upsert), which lets business roles reference permission codes
/// while the code stays the single source of truth.</para>
///
/// <para><b>Permissions describe actions, never UI visibility.</b> Every code
/// maps to a server-side check.</para>
/// </summary>
public static class PermissionCatalog
{
    /// <summary>Every system permission, ordered by module then code.</summary>
    public static readonly IReadOnlyList<CatalogPermission> All = Build();

    private static readonly IReadOnlyDictionary<string, CatalogPermission> ByCode =
        All.ToDictionary(p => p.Code, StringComparer.Ordinal);

    /// <summary>All permission codes.</summary>
    public static IReadOnlySet<string> AllCodes { get; } =
        All.Select(p => p.Code).ToHashSet(StringComparer.Ordinal);

    /// <summary>Does the given permission code exist in the catalogue?</summary>
    public static bool Exists(string? code) => !string.IsNullOrEmpty(code) && ByCode.ContainsKey(code!);

    /// <summary>Finds a catalogue entry, or null.</summary>
    public static CatalogPermission? Find(string? code) =>
        !string.IsNullOrEmpty(code) && ByCode.TryGetValue(code!, out var permission) ? permission : null;

    /// <summary>The role key assigned to a business's staff memberships during provisioning.</summary>
    public const string DefaultStaffRoleKey = "service_professional";

    /// <summary>The role key that owns the business.</summary>
    public const string OwnerRoleKey = "owner";

    private static IReadOnlyList<CatalogPermission> Build()
    {
        var list = new List<CatalogPermission>();
        foreach (var module in ModuleCatalog.Modules)
        foreach (var permission in module.Permissions)
        {
            list.Add(new CatalogPermission(
                Code: permission.Code,
                Name: $"{module.Name}: {Humanize(Action(permission.Code))}",
                Description: $"{Humanize(Action(permission.Code))} — {module.Description}",
                ModuleKey: module.Key));
        }

        return list
            .OrderBy(p => p.ModuleKey, StringComparer.Ordinal)
            .ThenBy(p => p.Code, StringComparer.Ordinal)
            .ToList();
    }

    private static string Action(string code)
    {
        var index = code.LastIndexOf('.');
        return index >= 0 && index < code.Length - 1 ? code[(index + 1)..] : code;
    }

    private static string Humanize(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;

        var builder = new StringBuilder(value.Length + 8);
        builder.Append(char.ToUpperInvariant(value[0]));
        for (var i = 1; i < value.Length; i++)
        {
            if (value[i] == '_')
            {
                builder.Append(' ');
                if (i + 1 < value.Length)
                {
                    builder.Append(char.ToUpperInvariant(value[i + 1]));
                    i++;
                }
                continue;
            }

            builder.Append(value[i]);
        }

        return builder.ToString();
    }

    /// <summary>
    /// The platform's default role templates. Seeded per business on
    /// provisioning and idempotently re-checked whenever roles are resolved,
    /// so an existing business can never be left without an owner role.
    ///
    /// <para><b>Owner</b> holds every permission implicitly (see
    /// <see cref="RoleTemplate.IsOwnerRole"/>) and its permission list is
    /// deliberately empty — a newly added permission is therefore automatically
    /// available to owners with no data migration.</para>
    ///
    /// <para><b>Manager</b> is deliberately powerful but cannot record payouts,
    /// change business settings, or administer roles: those are exactly the
    /// operations that move money or hand out authority.</para>
    /// </summary>
    public static readonly IReadOnlyList<RoleTemplate> DefaultRoles = new RoleTemplate[]
    {
        new(
            Key: OwnerRoleKey, Name: "Business Owner",
            Description: "Full authority over this business. Always holds every permission.",
            IsOwnerRole: true, SortOrder: 0,
            Permissions: Array.Empty<string>()),

        new(
            Key: "manager", Name: "Manager",
            Description: "Runs day-to-day operations: appointments, customers, staff, services, "
                       + "commissions and reports. Cannot record payouts, change settings or administer roles.",
            IsOwnerRole: false, SortOrder: 10,
            Permissions: new[]
            {
                "appointments.view", "appointments.update", "appointments.confirm",
                "appointments.cancel", "appointments.complete", "appointments.no_show",
                "appointments.reschedule", "appointments.approve_reschedule",
                "customers.view", "customers.manage", "customers.delete",
                "staff.view", "staff.manage", "staff.positions.manage",
                "serviceCatalog.view", "serviceCatalog.manage",
                "serviceCategories.view", "serviceCategories.manage",
                "attendance.view", "attendance.clock", "attendance.manage",
                "payments.view", "payments.create", "payments.confirm_cash",
                "loyalty.view", "loyalty.stamp", "loyalty.redeem",
                "stamps.view", "stamps.award", "stamps.adjust",
                "rewards.view", "redemptions.fulfill",
                "reviews.view", "reviews.manage",
                "notifications.view", "notifications.manage",
                "analytics.view",
                "commissions.view", "commissions.manage", "commissions.approve",
                "roles.view",
            }),

        new(
            Key: "receptionist", Name: "Receptionist",
            Description: "Front desk: books and manages appointments, looks after customers and takes payments.",
            IsOwnerRole: false, SortOrder: 20,
            Permissions: new[]
            {
                "appointments.view", "appointments.update", "appointments.confirm",
                "appointments.cancel", "appointments.complete", "appointments.no_show",
                "appointments.reschedule", "appointments.approve_reschedule",
                "customers.view", "customers.manage",
                "staff.view",
                "serviceCatalog.view", "serviceCategories.view",
                "attendance.view", "attendance.clock",
                "payments.view", "payments.create", "payments.confirm_cash",
                "notifications.view",
            }),

        new(
            Key: DefaultStaffRoleKey, Name: "Service Professional",
            Description: "Delivers services: works their own appointments, records stamps and sees their own earnings.",
            IsOwnerRole: false, SortOrder: 30,
            Permissions: new[]
            {
                "appointments.view", "appointments.update", "appointments.confirm",
                "appointments.cancel", "appointments.complete", "appointments.no_show",
                "appointments.reschedule",
                "customers.view",
                "staff.view",
                "serviceCatalog.view", "serviceCategories.view",
                "attendance.view", "attendance.clock",
                "stamps.view", "stamps.award",
                "loyalty.stamp", "loyalty.redeem",
                "rewards.view", "redemptions.fulfill",
                "reviews.view",
                "notifications.view",
                "commissions.view",
            }),
    };

    /// <summary>Finds a default role template by key.</summary>
    public static RoleTemplate? FindTemplate(string key) =>
        DefaultRoles.FirstOrDefault(r => r.Key.Equals(key, StringComparison.Ordinal));
}

