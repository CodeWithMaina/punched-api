using System.Text.RegularExpressions;

namespace PunchedApi.Tests;

/// <summary>
/// Step 9.4 acceptance gate (G18) + module-system adoption audit (G15/G19).
/// Fails the build if:
///  - any module-rooted controller lacks [RequireModule] (audit report is
///    printed to stdout listing each surface and its gate);
///  - a platform controller (Auth/SSE/Admin/…) wrongly carries it;
///  - the removed enforcement toggle config key reappears anywhere;
///  - the removed back-compat pro grant is re-introduced;
///  - the frontend renders hardcoded module pricing instead of plan-driven
///    pricing from GET /v1/plans;
///  - MODULE_SYSTEM_STATUS_AND_PLAN.md §4 has unfinished checkboxes.
/// </summary>
public class ModuleAdoptionAcceptanceTests
{
    // The workspace folders were renamed (PunchedApi → punched-api,
    // punched-pwd → punched-ui), so the root is resolved from the project file
    // instead of matching hardcoded folder names.
    private const string ApiProjectFile = "PunchedApi.csproj";
    private const string TestsProjectFile = "PunchedApi.Tests.csproj";

    /// <summary>Directory of the nearest ancestor holding <paramref name="projectFile"/>.</summary>
    private static string ProjectDir(string projectFile)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, projectFile)))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException(
                $"{projectFile} not found above {AppContext.BaseDirectory}.");
    }

    private static string ApiProjectDir => ProjectDir(ApiProjectFile);

    private static string TestsProjectDir => ProjectDir(TestsProjectFile);

    /// <summary>Workspace root: holds the API project, the test project and the frontend tree.</summary>
    private static string RepoRoot
    {
        get
        {
            var apiDir = ApiProjectDir;
            return Directory.GetParent(apiDir)?.FullName
                ?? throw new InvalidOperationException($"Parent of {apiDir} not found.");
        }
    }

    /// <summary>The frontend tree, or null when it is not checked out beside the API.</summary>
    private static string? FrontendRoot =>
        new[] { "punched-ui", "punched-pwd" }
            .Select(name => Path.Combine(RepoRoot, name))
            .FirstOrDefault(Directory.Exists);

    private static string[] ControllerFiles
    {
        get
        {
            var controllersDir = Path.Combine(ApiProjectDir, "API", "Controllers");
            return Directory.Exists(controllersDir)
                ? Directory.GetFiles(controllersDir, "*.cs", SearchOption.AllDirectories)
                : Array.Empty<string>();
        }
    }

    // ── Module-rooted controllers (must carry [RequireModule]) ──
    private static readonly (string File, string ModuleKey)[] ModuleRootedControllers =
    {
        ("BusinessController.Analytics.cs", "analytics"),
        ("BusinessController.Appointments.cs", "appointments"),
        ("BusinessController.Customers.cs", "customers"),
        ("BusinessController.Staff.cs", "staff"),
        ("AppointmentController.cs", "appointments"),
        ("LoyaltyCardController.cs", "loyalty"),
        ("LoyaltyProgramController.cs", "loyalty"),
        ("RedemptionController.cs", "rewards"),
        ("ReferralController.cs", "referral"),
        ("StampController.cs", "stamps"),
        ("ServiceCatalogController.cs", "serviceCatalog"),
        ("QrController.cs", "customers"),
        ("InvitationControllers.cs", "staff"),
        ("ReviewController.cs", "reviews"),
        ("LandingPageController.cs", "storefront"),
        ("MediaController.cs", "gallery"),
    };

    // ── Platform controllers (must NOT carry [RequireModule]) ──
    private static readonly string[] PlatformControllers =
    {
        "AuthController.cs", "SseController.cs", "AdminController.cs",
        "UserController.cs",
        "ModulesController.cs", "AdminModulesController.cs", "SubscriptionController.cs",
    };

    private static bool IsAcceptanceTestFile(string file) =>
        string.Equals(Path.GetFileName(file), "ModuleAdoptionAcceptanceTests.cs", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void EveryModuleRootedController_CarriesRequireModule()
    {
        var report = new System.Text.StringBuilder();
        report.AppendLine("═══ Module adoption report — backend controllers ═══");

        var failures = new List<string>();
        foreach (var (file, moduleKey) in ModuleRootedControllers)
        {
            var path = ControllerFiles.FirstOrDefault(f =>
                string.Equals(Path.GetFileName(f), file, StringComparison.OrdinalIgnoreCase));
            if (path == null)
            {
                failures.Add($"Missing module-rooted controller file: {file}");
                continue;
            }

            var content = File.ReadAllText(path);
            var gated = content.Contains("RequireModule");
            report.AppendLine($"  {file,-42} module={moduleKey,-14} gate={(gated ? "[RequireModule]" : "MISSING")}");
            if (!gated)
                failures.Add($"{file} is module-rooted ('{moduleKey}') but has no [RequireModule] gate.");
        }

        foreach (var file in PlatformControllers)
        {
            var path = ControllerFiles.FirstOrDefault(f =>
                string.Equals(Path.GetFileName(f), file, StringComparison.OrdinalIgnoreCase));
            if (path == null) continue;
            var content = File.ReadAllText(path);
            report.AppendLine($"  {file,-42} platform gate={(content.Contains("RequireModule") ? "UNEXPECTED [RequireModule]" : "none (correct)")}");
            if (content.Contains("RequireModule"))
                failures.Add($"Platform controller {file} must not carry [RequireModule].");
        }

        Console.WriteLine(report.ToString());
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void EnforcementToggle_ConfigKey_IsFullyRemoved()
    {
        var offenders = new List<string>();
        void Scan(string root)
        {
            if (!Directory.Exists(root)) return;
            foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                    file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                    IsAcceptanceTestFile(file)) // this file mentions the key in its assertions
                    continue;
                if (File.ReadAllText(file).Contains("EnforcementEnabled"))
                    offenders.Add(file);
            }
        }
        Scan(ApiProjectDir);
        Scan(TestsProjectDir);

        Assert.True(offenders.Count == 0,
            "The enforcement toggle was removed — 'EnforcementEnabled' must not appear anywhere. Offenders: " +
            string.Join(", ", offenders));
    }

    [Fact]
    public void BackCompatProGrant_IsFullyRemoved()
    {
        var seederPath = Path.Combine(ApiProjectDir, "Infrastructure", "Data", "Seeding", "ModuleCatalogSeeder.cs");
        var content = File.ReadAllText(seederPath);
        Assert.False(content.Contains("BackCompatGrantEnabled", StringComparison.Ordinal),
            "The back-compat pro grant was removed — ModuleCatalogSeeder must not reference BackCompatGrantEnabled.");
        Assert.False(content.Contains("Back-compat", StringComparison.Ordinal),
            "Back-compat grant log line is gone.");
    }

    [Fact]
    public void FrontendModulesPage_UsesPlanDrivenPricing_NoHardcodes()
    {
        var frontend = FrontendRoot;
        if (frontend == null) return; // frontend tree absent — skip
        var page = Path.Combine(frontend, "app", "dashboard", "business", "profile", "modules", "page.tsx");
        if (!File.Exists(page)) return;

        var content = File.ReadAllText(page);
        Assert.False(Regex.IsMatch(content, @"\$\d+/mo"),
            "Hardcoded '$N/mo' pricing must not reappear; pricing comes from GET /v1/plans.");
        Assert.False(content.Contains("ADDON_PRICING"), "Static ADDON_PRICING map was deleted.");
        Assert.True(content.Contains("plansApi"),
            "The modules page must derive pricing from the plans API.");
    }

    [Fact]
    public void StatusPlanSection4_AllCheckboxesComplete()
    {
        var doc = Path.Combine(RepoRoot, "MODULE_SYSTEM_STATUS_AND_PLAN.md");
        if (!File.Exists(doc)) return;

        var section4 = false;
        var unfinished = new List<string>();
        foreach (var line in File.ReadLines(doc))
        {
            if (line.StartsWith("## ")) section4 = line.Contains("4");
            // Items explicitly tagged <!-- env-gated --> require external
            // infrastructure (e.g. Docker for testcontainers) and are tracked
            // separately; they do not block the acceptance gate.
            if (section4 && (line.Contains("- [ ]") || line.Contains("* [ ]")) && !line.Contains("env-gated"))
                unfinished.Add(line.Trim());
        }

        Assert.True(unfinished.Count == 0,
            "MODULE_SYSTEM_STATUS_AND_PLAN.md §4 still has unfinished items:" + Environment.NewLine +
            string.Join(Environment.NewLine, unfinished));
    }
}
