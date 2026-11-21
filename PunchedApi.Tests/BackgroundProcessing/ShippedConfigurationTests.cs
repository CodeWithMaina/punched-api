using Microsoft.Extensions.Configuration;
using PunchedApi.Application.BackgroundProcessing;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Tests.BackgroundProcessing;

/// <summary>
/// Guards the configuration that actually ships. A typo in <c>appsettings.json</c>
/// (an unknown worker name, a floor inside Neon's suspend window, a pool larger than
/// the declared plan) would otherwise only surface when a container starts.
/// </summary>
public class ShippedConfigurationTests
{
    private static string FindContentRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "appsettings.json"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("appsettings.json was not found above the test output directory.");
    }

    private static IConfiguration AppSettings() =>
        new ConfigurationBuilder()
            .SetBasePath(FindContentRoot())
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

    [Fact]
    public void The_shipped_appsettings_resolves_a_valid_profile_for_every_worker()
    {
        var options = new BackgroundProcessingOptions(AppSettings());

        Assert.Empty(options.Validate());

        // Every worker name the application uses must be configurable. A missing entry
        // is not fatal, but a *misspelled* one silently loses its override, so the set
        // of resolved names is asserted explicitly.
        foreach (var name in BackgroundProcessingOptions.AllWorkers)
        {
            Assert.Contains(name, options.Profiles.Keys);
        }

        Assert.Equal(15, options.For(BackgroundProcessingOptions.Names.Notifications).MaxIdleIntervalSeconds);
        Assert.Equal(3600, options.For(BackgroundProcessingOptions.Names.Cleanup).MaxIdleIntervalSeconds);
    }

    [Fact]
    public void The_shipped_appsettings_fits_the_free_plan_budget_in_both_roles()
    {
        var budget = AppSettings().GetSection(DatabaseBudget.SectionName).Get<DatabaseBudget>();

        Assert.NotNull(budget);
        Assert.Empty(budget!.Validate("api", roleMaximumPoolSize: 8, roleConnectionBudget: 12));
        Assert.Empty(budget.Validate("worker", roleMaximumPoolSize: 8, roleConnectionBudget: 12));
    }

    [Fact]
    public void The_production_free_profile_is_valid()
    {
        // Mirrors infra/config/production.env.example (Free plan, scale-to-zero on).
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BackgroundProcessing:ScaleToZeroFriendlyMode"] = "true",
                ["BackgroundProcessing:MaxIdleIntervalSeconds"] = "360",
                ["BackgroundProcessing:ScaleToZeroIdleFloorSeconds"] = "360",
                ["BackgroundProcessing:ActiveIntervalSeconds"] = "2",
                ["BackgroundProcessing:FirstIdleIntervalSeconds"] = "10",
                ["Database:DeploymentConnectionBudget"] = "105",
                ["Database:ApiPoolSize"] = "8",
                ["Database:WorkerPoolSize"] = "6",
                ["Database:ApiReplicas"] = "1",
                ["Database:WorkerReplicas"] = "1",
                ["Database:MigrationConnections"] = "2",
                ["Database:MaintenanceConnections"] = "2"
            })
            .Build();

        var options = new BackgroundProcessingOptions(configuration);
        Assert.Empty(options.Validate());
        Assert.Equal(360, options.For(BackgroundProcessingOptions.Names.Notifications).EffectiveMaxIdleIntervalSeconds);
        Assert.Equal(360, options.For(BackgroundProcessingOptions.Names.Analytics).EffectiveMaxIdleIntervalSeconds);

        var budget = configuration.GetSection(DatabaseBudget.SectionName).Get<DatabaseBudget>()!;
        Assert.Empty(budget.Validate("worker", roleMaximumPoolSize: 6, roleConnectionBudget: 40));
    }

    [Fact]
    public void The_production_paid_profile_is_valid_and_latency_first()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BackgroundProcessing:ScaleToZeroFriendlyMode"] = "false",
                ["BackgroundProcessing:MaxIdleIntervalSeconds"] = "15",
                ["Database:DeploymentConnectionBudget"] = "443",
                ["Database:ApiPoolSize"] = "16",
                ["Database:WorkerPoolSize"] = "10"
            })
            .Build();

        var options = new BackgroundProcessingOptions(configuration);

        Assert.Empty(options.Validate());
        Assert.False(options.For(BackgroundProcessingOptions.Names.Notifications).ScaleToZeroFriendly);
        Assert.Equal(15, options.For(BackgroundProcessingOptions.Names.Notifications).EffectiveMaxIdleIntervalSeconds);

        var budget = configuration.GetSection(DatabaseBudget.SectionName).Get<DatabaseBudget>()!;
        Assert.Empty(budget.Validate("api", roleMaximumPoolSize: 16, roleConnectionBudget: 40));
    }
}
