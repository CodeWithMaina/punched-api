namespace PunchedApi.Application.Settings;

public enum SeedExecutionMode
{
    Idempotent = 0,
    ResetDatabase = 1,
    ClearExistingData = 2,
    AppendData = 3,
}

public sealed class SeedOptions
{
    public const string SectionName = "Seed";

    public bool Enabled { get; set; }

    /// <summary>
    /// When false (default), ordinary API startup never runs synthetic-data
    /// seeding. Seeding then requires the explicit CLI
    /// (<c>dotnet run -- db seed</c>). This keeps boot bounded and predictable.
    /// </summary>
    public bool RunOnStartup { get; set; } = false;

    /// <summary>
    /// When false (default), the seeder skips the historical analytics backfill
    /// step even when seeding runs. Backfills must be invoked explicitly via
    /// <c>dotnet run -- db backfill --from ... --to ...</c>.
    /// </summary>
    public bool IncludeAnalyticsBackfill { get; set; } = false;

    /// <summary>
    /// When false (default), the Aurelia demo <b>activity</b> step — the generated
    /// appointment book, its payments, attendance, reviews, loyalty ledger and
    /// engagement rows — is skipped even when seeding runs. The demo tenant's
    /// profile, services and staff are still seeded; only synthetic activity is
    /// opt-in, so an ordinary <c>db seed</c> can never repopulate a business's
    /// calendar with bookings nobody made.
    /// </summary>
    public bool EnableDemoActivity { get; set; } = false;

    public bool ResetDatabase { get; set; }
    public bool ClearExistingData { get; set; }
    public bool AppendData { get; set; }
    public bool GenerateReport { get; set; } = true;
    public int? RandomSeed { get; set; } = 12345;
    public int BusinessCount { get; set; } = 5;
    public bool VerboseLogging { get; set; } = true;
    public string ReportPath { get; set; } = "seed-report.json";

    public SeedExecutionMode ResolveMode()
    {
        if (ResetDatabase)
        {
            return SeedExecutionMode.ResetDatabase;
        }

        if (ClearExistingData)
        {
            return SeedExecutionMode.ClearExistingData;
        }

        if (AppendData)
        {
            return SeedExecutionMode.AppendData;
        }

        return SeedExecutionMode.Idempotent;
    }
}
