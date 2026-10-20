using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PunchedApi.Application.Settings;

namespace PunchedApi.Infrastructure.Data.Seeding;

public sealed class DatabaseSeeder : IDatabaseSeeder
{
    private static readonly HashSet<string> NonTransactionalSteps = new(StringComparer.OrdinalIgnoreCase)
    {
        "DatabasePreparation",
        "ValidationAndReport",
        "UnsupportedDomains",
    };

    private readonly ApplicationDbContext _db;
    private readonly IOptions<SeedOptions> _options;
    private readonly IWebHostEnvironment _environment;
    private readonly ISeedRandom _random;
    private readonly IEnumerable<ISeedStep> _steps;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DatabaseSeeder> _logger;

    public DatabaseSeeder(
        ApplicationDbContext db,
        IOptions<SeedOptions> options,
        IWebHostEnvironment environment,
        ISeedRandom random,
        IEnumerable<ISeedStep> steps,
        IServiceProvider serviceProvider,
        ILogger<DatabaseSeeder> logger)
    {
        _db = db;
        _options = options;
        _environment = environment;
        _random = random;
        _steps = steps;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task RunAsync(bool explicitInvocation = false, CancellationToken cancellationToken = default)
    {
        var options = _options.Value;

        if (_environment.IsProduction() && !explicitInvocation)
        {
            _logger.LogInformation("Database seeding skipped. Environment is Production.");
            return;
        }

        if (_environment.IsProduction() && (options.ResetDatabase || options.ClearExistingData))
        {
            throw new InvalidOperationException(
                "Refusing destructive seed preparation in Production " +
                "(Seed:ResetDatabase / Seed:ClearExistingData must be false).");
        }

        if (!options.Enabled)
        {
            _logger.LogInformation(
                "Database seeding skipped (Seed.Enabled is false). " +
                "Synthetic seeding is disabled during normal API startup by default. " +
                "To run it explicitly: dotnet run -- db seed (Development only).");
            return;
        }

        if (!explicitInvocation && !options.RunOnStartup)
        {
            _logger.LogInformation(
                "Database seeding skipped on startup (Seed.RunOnStartup is false). " +
                "Normal startup stays bounded; run synthetic seeding explicitly via: " +
                "dotnet run -- db seed. Historical analytics backfill is never part of startup; " +
                "run it via: dotnet run -- db backfill --from <yyyy-MM-dd> --to <yyyy-MM-dd>.");
            return;
        }

        var selectedCount = Math.Clamp(options.BusinessCount, 1, SeedCatalog.MaxBusinesses);
        if (selectedCount != options.BusinessCount)
        {
            _logger.LogWarning("Requested BusinessCount {Requested} adjusted to supported range 1..{Max}.", options.BusinessCount, SeedCatalog.MaxBusinesses);
        }

        var scenario = SeedCatalog.BuildScenario(selectedCount);
        var reportPath = Path.IsPathRooted(options.ReportPath)
            ? options.ReportPath
            : Path.Combine(_environment.ContentRootPath, options.ReportPath);

        var report = new SeedReport
        {
            GenerationDateUtc = DateTime.UtcNow,
            RandomSeed = options.RandomSeed ?? _random.ActualSeed,
            Mode = options.ResolveMode(),
            BusinessesRequested = selectedCount,
        };

        foreach (var capability in CapabilityMatrix.Build())
        {
            report.CapabilityAnalysis.Add(capability);
        }

        var context = new SeedExecutionContext
        {
            Db = _db,
            Environment = _environment,
            Logger = _logger,
            Random = _random,
            Report = report,
            ReportOutputPath = reportPath,
            Scenario = scenario,
            ServiceProvider = _serviceProvider,
        };

        var watch = Stopwatch.StartNew();

        try
        {
            _logger.LogInformation("Starting database seed run. Mode={Mode}, Businesses={BusinessCount}, Seed={Seed}, Explicit={Explicit}",
                report.Mode,
                selectedCount,
                report.RandomSeed,
                explicitInvocation);

            if (explicitInvocation && _environment.IsProduction())
            {
                _logger.LogInformation("Seed run is executing via explicit CLI invocation in Production environment.");
            }

            var steps = _steps.ToList();
            if (steps.Count == 0)
            {
                _logger.LogWarning("No seed steps registered. Skipping seeding logic.");
                return;
            }

            // Run database prep outside any transaction (reset/clear may require DDL).

            var prepStep = steps[0];
            var prepWatch = Stopwatch.StartNew();
            _logger.LogInformation("Running seed step: {Step}", prepStep.Name);
            await prepStep.ExecuteAsync(context, cancellationToken);
            _logger.LogInformation("Completed seed step: {Step} in {ElapsedMs}ms", prepStep.Name, prepWatch.ElapsedMilliseconds);

            // Per-step bounded transactions (never one transaction across the run).

            foreach (var step in steps.Skip(1))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (step.Name.Equals("AnalyticsBackfill", StringComparison.OrdinalIgnoreCase)
                    && !options.IncludeAnalyticsBackfill)
                {
                    _logger.LogInformation(
                        "Skipping seed step: {Step} (Seed.IncludeAnalyticsBackfill is false). " +
                        "Run historical analytics explicitly via: " +
                        "dotnet run -- db backfill --from <yyyy-MM-dd> --to <yyyy-MM-dd>.",
                        step.Name);
                    continue;
                }

                var stepWatch = Stopwatch.StartNew();
                _logger.LogInformation("Running seed step: {Step}", step.Name);

                if (NonTransactionalSteps.Contains(step.Name))
                {
                    await step.ExecuteAsync(context, cancellationToken);
                }
                else
                {
                    await using var stepTx = await _db.Database.BeginTransactionAsync(cancellationToken);
                    try
                    {
                        await step.ExecuteAsync(context, cancellationToken);
                        await stepTx.CommitAsync(cancellationToken);
                    }
                    catch
                    {
                        try { await stepTx.RollbackAsync(cancellationToken); } catch { }
                        throw;
                    }
                }

                _logger.LogInformation("Completed seed step: {Step} in {ElapsedMs}ms", step.Name, stepWatch.ElapsedMilliseconds);
            }

            watch.Stop();
            report.ExecutionTime = watch.Elapsed;

            if (options.GenerateReport)
            {
                await SeedReportWriter.WriteAsync(report, reportPath, cancellationToken);
                _logger.LogInformation("Seed report generated at {ReportPath}", reportPath);
            }

            _logger.LogInformation("Database seeding completed successfully in {ElapsedMs}ms", watch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            report.Errors.Add(ex.Message);
            _logger.LogError(ex, "Database seeding failed.");

            if (options.GenerateReport)
            {
                try
                {
                    watch.Stop();
                    report.ExecutionTime = watch.Elapsed;
                    await SeedReportWriter.WriteAsync(report, reportPath, cancellationToken);
                }
                catch
                {
                    // Ignore report-write failure after primary exception.
                }
            }

            throw;
        }
    }
}
