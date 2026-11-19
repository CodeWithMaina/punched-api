using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PunchedApi.Application.Analytics;
using PunchedApi.Application.Services;

namespace PunchedApi.Infrastructure.Data.Seeding;

public sealed class DatabaseCliRunner : IDatabaseCliRunner
{
    private static readonly HashSet<string> SupportedCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "db",
        "data",
        "migrate",
        "seed",
        "migrate-seed",
        "init-db",
        "backfill",
    };

    private readonly ApplicationDbContext _dbContext;
    private readonly IDatabaseSeeder _databaseSeeder;
    private readonly IAdminBootstrapper _adminBootstrapper;
    private readonly IServiceProvider _serviceProvider;
    private readonly IHostEnvironment _environment;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DatabaseCliRunner> _logger;

    public DatabaseCliRunner(
        ApplicationDbContext dbContext,
        IDatabaseSeeder databaseSeeder,
        IAdminBootstrapper adminBootstrapper,
        IServiceProvider serviceProvider,
        IHostEnvironment environment,
        IConfiguration configuration,
        ILogger<DatabaseCliRunner> logger)
    {
        _dbContext = dbContext;
        _databaseSeeder = databaseSeeder;
        _adminBootstrapper = adminBootstrapper;
        _serviceProvider = serviceProvider;
        _environment = environment;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<bool> TryRunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        if (args.Length == 0)
        {
            return false;
        }

        if (!SupportedCommands.Contains(args[0]))
        {
            return false;
        }

        var normalized = NormalizeCommand(args);
        if (normalized == null)
        {
            PrintHelp();
            return true;
        }

        switch (normalized)
        {
            case "migrate":
                await MigrateAsync(cancellationToken);
                break;
            case "seed":
                await SeedAsync(cancellationToken);
                break;
            case "migrate-seed":
                await MigrateAsync(cancellationToken);
                await SeedAsync(cancellationToken);
                break;
            case "backfill":
                await BackfillAsync(args, cancellationToken);
                break;
            case "purge-test-appointments":
                await PurgeTestAppointmentsAsync(args, cancellationToken);
                break;
            case "help":
                PrintHelp();
                break;
            default:
                PrintHelp();
                break;
        }

        return true;
    }

    private async Task MigrateAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Running CLI database migration command.");
        await _dbContext.Database.MigrateAsync(cancellationToken);
        await _adminBootstrapper.EnsureDefaultAdminAsync(cancellationToken);
        _logger.LogInformation("CLI database migration command completed.");
    }

    private async Task SeedAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Running CLI database seed command.");
        await _databaseSeeder.RunAsync(explicitInvocation: true, cancellationToken);
        await _adminBootstrapper.EnsureDefaultAdminAsync(cancellationToken);
        _logger.LogInformation("CLI database seed command completed.");
    }

    private async Task BackfillAsync(string[] args, CancellationToken cancellationToken)
    {
        var parsed = ParseBackfillArgs(args);
        LogTargetDatabase();
        _logger.LogInformation(
            "Running CLI analytics backfill. From={From} To={To} Business={Business} Resume={Resume}",
            parsed.From.ToString("yyyy-MM-dd"), parsed.To.ToString("yyyy-MM-dd"),
            parsed.BusinessId?.ToString() ?? "all", parsed.Resume);

        var runner = _serviceProvider.GetRequiredService<IAnalyticsBackfillRunner>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        var result = await runner.RunAsync(new AnalyticsBackfillOptions
        {
            BusinessId = parsed.BusinessId,
            From = parsed.From,
            To = parsed.To,
            Resume = parsed.Resume,
        }, cts.Token);

        _logger.LogInformation(
            "CLI analytics backfill finished. Businesses={B} ProcessedDays={P} SkippedDays={S} ElapsedMs={M}",
            result.BusinessesProcessed, result.DaysProcessed, result.DaysSkipped, (long)result.Elapsed.TotalMilliseconds);

        if (!result.Success)
        {
            throw new InvalidOperationException(result.Error ?? "Analytics backfill failed.");
        }
    }

    private async Task PurgeTestAppointmentsAsync(string[] args, CancellationToken cancellationToken)
    {
        var (identifier, apply, seedScope, maxSeedIndex, marker, demoDomains) = ParsePurgeArgs(args);

        if (string.IsNullOrWhiteSpace(identifier))
        {
            throw new ArgumentException(
                "purge-test-appointments requires --business <id|slug>. Example: "
                + "dotnet PunchedApi.dll db purge-test-appointments --business aurelia-luxe [--apply]");
        }

        var businessId = Guid.TryParse(identifier, out var parsedId)
            ? parsedId
            : await _dbContext.Businesses
                .Where(b => b.Slug != null && b.Slug == identifier)
                .Select(b => b.Id)
                .FirstOrDefaultAsync(cancellationToken);

        if (businessId == Guid.Empty)
        {
            throw new ArgumentException($"No business matches '{identifier}'. Pass the business id or slug.");
        }

        LogTargetDatabase();
        _logger.LogInformation(
            "Purging test appointments. Business={Business} Apply={Apply} SeedScope={Scope} VerificationMarker={Marker}",
            businessId, apply, seedScope, marker);

        var cleanup = _serviceProvider.GetRequiredService<ITestAppointmentCleanupService>();
        var report = await cleanup.ExecuteAsync(
            new TestAppointmentCleanupRequest(
                businessId, apply, seedScope, maxSeedIndex, marker, DemoCustomerEmailDomains: demoDomains),
            cancellationToken);

        _logger.LogInformation(
            "Appointments for {Business}: total={Total} seeded={Seeded} verification={Verification} automation={Automation} demo-identities={Synthetic} retained={Retained}",
            report.BusinessName, report.TotalAppointments, report.SeededAppointments,
            report.VerificationAppointments, report.AutomationAppointments,
            report.SyntheticIdentityAppointments, report.RetainedAppointments);

        if (report.RemovedRows.Count > 0)
        {
            foreach (var (table, count) in report.RemovedRows.OrderBy(r => r.Key, StringComparer.Ordinal))
            {
                _logger.LogInformation("  removed {Table}: {Count}", table, count);
            }
        }

        _logger.LogInformation(
            "Remaining appointments by status: {Statuses}",
            report.RemainingByStatus.Count == 0
                ? "none"
                : string.Join(", ", report.RemainingByStatus.OrderBy(r => r.Key, StringComparer.Ordinal)
                    .Select(r => $"{r.Key}={r.Value}")));

        foreach (var warning in report.Warnings)
        {
            _logger.LogWarning("{Warning}", warning);
        }
    }

    private static (string? Identifier, bool Apply, string SeedScope, int MaxSeedIndex, string Marker,
        IReadOnlyList<string> DemoDomains) ParsePurgeArgs(string[] args)
    {
        string? identifier = null;
        var apply = false;
        var seedScope = "aurelia-demo";
        var maxSeedIndex = 5000;
        var marker = "Seeded verification booking.";
        var demoDomains = new List<string> { "demo.punched.app" };

        for (var i = 0; i < args.Length; i++)
        {
            if (i + 1 < args.Length && args[i].Equals("--business", StringComparison.OrdinalIgnoreCase))
            {
                identifier = args[i + 1];
            }
            else if (i + 1 < args.Length && args[i].Equals("--seed-scope", StringComparison.OrdinalIgnoreCase))
            {
                seedScope = args[i + 1];
            }
            else if (i + 1 < args.Length && args[i].Equals("--demo-customer-domain", StringComparison.OrdinalIgnoreCase))
            {
                demoDomains = args[i + 1]
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();
            }
            else if (i + 1 < args.Length && args[i].Equals("--max-seed-index", StringComparison.OrdinalIgnoreCase)
                     && int.TryParse(args[i + 1], out var parsedMax) && parsedMax > 0)
            {
                maxSeedIndex = parsedMax;
            }
            else if (i + 1 < args.Length && args[i].Equals("--verification-marker", StringComparison.OrdinalIgnoreCase))
            {
                marker = args[i + 1];
            }
            else if (args[i].Equals("--apply", StringComparison.OrdinalIgnoreCase))
            {
                apply = true;
            }
        }

        return (identifier, apply, seedScope, maxSeedIndex, marker, demoDomains);
    }

    private void LogTargetDatabase()
    {
        // Identify the target without logging secrets: environment + host/db name only.
        string? host = null;
        string? database = null;
        try
        {
            var databaseUrl = _configuration["DATABASE_URL"];
            string? connectionString = null;
            if (!string.IsNullOrWhiteSpace(databaseUrl) && Uri.TryCreate(databaseUrl, UriKind.Absolute, out var uri))
            {
                host = uri.Host;
                database = uri.AbsolutePath.Trim('/');
            }
            else
            {
                connectionString = _configuration.GetConnectionString("DefaultConnection");
                if (!string.IsNullOrWhiteSpace(connectionString))
                {
                    var csb = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);
                    host = csb.Host;
                    database = csb.Database;
                }
            }
        }
        catch
        {
            // Never fail the command just because target logging failed.
        }

        _logger.LogInformation(
            "Backfill target. Environment={Env} Host={Host} Database={Db}",
            _environment.EnvironmentName, host ?? "(unknown)", database ?? "(unknown)");
    }

    private static (DateOnly From, DateOnly To, Guid? BusinessId, bool Resume) ParseBackfillArgs(string[] args)
    {
        DateOnly? from = null;
        DateOnly? to = null;
        Guid? businessId = null;
        var resume = true;

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--from", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length
                && DateOnly.TryParse(args[i + 1], out var f))
            {
                from = f;
            }
            if (args[i].Equals("--to", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length
                && DateOnly.TryParse(args[i + 1], out var t))
            {
                to = t;
            }
            if (args[i].Equals("--business", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length
                && Guid.TryParse(args[i + 1], out var b))
            {
                businessId = b;
            }
            if (args[i].Equals("--no-resume", StringComparison.OrdinalIgnoreCase))
            {
                resume = false;
            }
        }

        if (from == null || to == null)
        {
            throw new ArgumentException("Backfill requires --from <yyyy-MM-dd> and --to <yyyy-MM-dd>. Example: db backfill --from 2026-01-01 --to 2026-03-01");
        }

        if (to < from)
        {
            throw new ArgumentException("'--to' must be greater than or equal to '--from'.");
        }

        return (from.Value, to.Value, businessId, resume);
    }

    private static string? NormalizeCommand(string[] args)
    {
        if (args.Length == 0)
        {
            return null;
        }

        if (args[0].Equals("backfill", StringComparison.OrdinalIgnoreCase))
        {
            return "backfill";
        }

        // Data hygiene runs as `data purge-test-appointments ...` (and as
        // `db purge-test-appointments ...` for symmetry with the other db commands).
        if (args[0].Equals("data", StringComparison.OrdinalIgnoreCase))
        {
            return args.Length > 1 && args[1].Equals("purge-test-appointments", StringComparison.OrdinalIgnoreCase)
                ? "purge-test-appointments"
                : null;
        }

        if (args[0].Equals("migrate", StringComparison.OrdinalIgnoreCase))
        {
            return "migrate";
        }

        if (args[0].Equals("seed", StringComparison.OrdinalIgnoreCase))
        {
            return "seed";
        }

        if (args[0].Equals("migrate-seed", StringComparison.OrdinalIgnoreCase) ||
            args[0].Equals("init-db", StringComparison.OrdinalIgnoreCase))
        {
            return "migrate-seed";
        }

        if (!args[0].Equals("db", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (args.Length == 1)
        {
            return "help";
        }

        return args[1].ToLowerInvariant() switch
        {
            "migrate" => "migrate",
            "seed" => "seed",
            "migrate-seed" => "migrate-seed",
            "backfill" => "backfill",
            "purge-test-appointments" => "purge-test-appointments",
            "init" => "migrate-seed",
            "help" => "help",
            _ => null,
        };
    }

    private void PrintHelp()
    {
        _logger.LogInformation("CLI database commands:");
        _logger.LogInformation("  dotnet PunchedApi.dll db migrate");
        _logger.LogInformation("  dotnet PunchedApi.dll db seed");
        _logger.LogInformation("  dotnet PunchedApi.dll db migrate-seed");
        _logger.LogInformation("  dotnet PunchedApi.dll db backfill --from <yyyy-MM-dd> --to <yyyy-MM-dd> [--business <id>] [--no-resume]");
        _logger.LogInformation("  dotnet PunchedApi.dll db purge-test-appointments --business <id|slug> [--apply] [--seed-scope aurelia-demo] [--max-seed-index 5000] [--verification-marker <text>]");
        _logger.LogInformation("  dotnet PunchedApi.dll data purge-test-appointments --business <id|slug> [--apply]");
        _logger.LogInformation("  dotnet PunchedApi.dll migrate");
        _logger.LogInformation("  dotnet PunchedApi.dll seed");
        _logger.LogInformation("  dotnet PunchedApi.dll migrate-seed");
        _logger.LogInformation("  dotnet PunchedApi.dll backfill --from <yyyy-MM-dd> --to <yyyy-MM-dd>");
        _logger.LogInformation("When running in Docker, pass Seed__Enabled=true for seed commands.");
    }
}
