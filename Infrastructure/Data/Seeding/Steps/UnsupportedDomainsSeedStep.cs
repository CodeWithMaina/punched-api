namespace PunchedApi.Infrastructure.Data.Seeding.Steps;

/// <summary>
/// Records which optional domains the current schema does <b>not</b> support, so
/// the seed report never implies more coverage than the platform actually has.
///
/// This list is deliberately short: every domain that used to be listed here
/// (services, appointments, payments, notifications, reviews, attendance,
/// commissions) now exists in the schema and is seeded by
/// <c>AureliaLuxeSetupSeedStep</c> / <c>AureliaLuxeActivitySeedStep</c>.
/// </summary>
public sealed class UnsupportedDomainsSeedStep : ISeedStep
{
    public string Name => "UnsupportedDomains";

    public Task ExecuteAsync(SeedExecutionContext context, CancellationToken cancellationToken)
    {
        var messages = new[] 
        {
            "InventorySeeder skipped: the platform ships no product/stock/supplier tables.",
            "Granular RBAC seeding skipped: authority is the membership role enum (Owner/Staff/Customer) plus the static PermissionMatrix — there is no role/permission table to seed.",
            "Commissions seeding skipped: the platform has no commission ledger; staff performance is derived from appointments, stamps and redemptions.",
            "Service categories are a catalogue label, not an entity: services carry their category in the description because service_catalog_items has no category column.",
            "Appointment status 'pending' is not produced by the backend: the lifecycle is draft/booked/confirmed/completed/cancelled/no_show, and reschedules are tracked as RescheduleRequest rows.",
        };

        foreach (var message in messages)
        {
            context.Report.Warnings.Add(message);
            context.Logger.LogInformation("{Message}", message);
        }

        return Task.CompletedTask;
    }
}
