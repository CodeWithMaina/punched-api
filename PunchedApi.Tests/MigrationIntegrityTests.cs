using System.Text.RegularExpressions;

namespace PunchedApi.Tests;

/// <summary>
/// Guards the shape of the business-operations migration.
///
/// <para>These assertions encode the guarantees the migration must keep for the
/// existing, populated database it was written against. They exist because the
/// generated migration was originally UNSAFE: the new NOT NULL columns on
/// <c>reschedule_requests</c> were added with sentinel defaults, so the CHECK
/// constraints and foreign keys added later in the same migration rejected every
/// pre-existing row. A generated migration cannot know how to backfill, so the
/// backfill has to be hand-written AND ordered correctly — which is exactly the
/// kind of thing a later regeneration would silently drop.</para>
/// </summary>
public class MigrationIntegrityTests
{
    private const string MigrationFileName = "20261031083613_AddCommissionsCategoriesPositionsAndRbac.cs";

    private static string ReadMigration()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, "Migrations", MigrationFileName);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate {MigrationFileName} by walking up from {AppContext.BaseDirectory}.");
    }

    private static string UpBody()
    {
        var source = ReadMigration();
        var downIndex = source.IndexOf("protected override void Down", StringComparison.Ordinal);
        Assert.True(downIndex > 0, "The migration must declare a Down method.");
        return source[..downIndex];
    }

    private static string DownBody()
    {
        var source = ReadMigration();
        var downIndex = source.IndexOf("protected override void Down", StringComparison.Ordinal);
        return source[downIndex..];
    }

    [Fact]
    public void The_Up_migration_never_drops_tables_columns_indexes_or_foreign_keys()
    {
        var up = UpBody();

        // Historical records are preserved: the upgrade path may only add,
        // rename or backfill — never destroy.
        Assert.DoesNotContain("migrationBuilder.DropTable", up);
        Assert.DoesNotContain("migrationBuilder.DropColumn", up);
        Assert.DoesNotContain("migrationBuilder.DropIndex", up);
        Assert.DoesNotContain("migrationBuilder.DropForeignKey", up);
        Assert.DoesNotContain("migrationBuilder.DropCheckConstraint", up);
    }

    [Fact]
    public void The_Up_migration_uses_data_preserving_renames()
    {
        var up = UpBody();

        Assert.Contains("migrationBuilder.RenameTable", up);
        Assert.Contains("newName: \"reschedule_requests\"", up);
        Assert.Contains("migrationBuilder.RenameColumn", up);
    }

    [Fact]
    public void The_Down_migration_reverses_the_renames()
    {
        var down = DownBody();

        Assert.Contains("newName: \"RescheduleRequests\"", down);
    }

    [Fact]
    public void Existing_reschedule_rows_are_backfilled_before_the_check_constraints()
    {
        var up = UpBody();

        var backfillIndex = up.IndexOf("UPDATE reschedule_requests AS r", StringComparison.Ordinal);
        Assert.True(backfillIndex > 0, "The Up migration must backfill existing reschedule rows.");

        // Search for the CALL, not the constraint name: the name also appears in
        // the explanatory comment that documents this very ordering requirement.
        var partyCheckIndex = up.IndexOf("migrationBuilder.AddCheckConstraint", StringComparison.Ordinal);
        Assert.True(partyCheckIndex > 0, "The migration must add its CHECK constraints.");

        Assert.True(
            backfillIndex < partyCheckIndex,
            "The backfill must run BEFORE the CHECK constraints, otherwise the " +
            "constraint rejects the existing rows and the whole migration aborts.");
    }

    [Fact]
    public void Existing_reschedule_rows_are_backfilled_before_the_foreign_keys()
    {
        var up = UpBody();

        var backfillIndex = up.IndexOf("UPDATE reschedule_requests AS r", StringComparison.Ordinal);
        var fkIndex = up.IndexOf("migrationBuilder.AddForeignKey", StringComparison.Ordinal);
        Assert.True(fkIndex > 0, "The migration must add its foreign keys.");
        Assert.True(
            backfillIndex < fkIndex,
            "The backfill must run BEFORE the business foreign key, otherwise the " +
            "sentinel all-zero business_id violates it.");
    }

    [Fact]
    public void The_backfill_derives_every_value_from_existing_data()
    {
        var up = UpBody();

        // business_id comes from the linked appointment (the authoritative tenant).
        Assert.Contains("business_id            = a.business_id", up);
        // The original schedule is the appointment's schedule at migration time.
        Assert.Contains("THEN a.scheduled_at", up);
        Assert.Contains("THEN a.end_at", up);
        Assert.Contains("COALESCE(r.original_staff_user_id, a.staff_user_id)", up);
        // updated_at falls back to created_at rather than the year-1 sentinel.
        Assert.Contains("THEN r.created_at", up);
    }

    [Fact]
    public void The_backfilled_party_value_matches_the_check_constraint_casing()
    {
        var up = UpBody();

        // The enum is persisted by NAME (HasConversion<string>), so the CHECK
        // expects PascalCase. A lowercase backfill would abort the migration.
        var check = Regex.Match(up, @"requested_by_party IN \(([^)]*)\)");
        Assert.True(check.Success, "ck_reschedule_requests_party must be present.");

        var allowed = Regex.Matches(check.Groups[1].Value, @"'([^']+)'")
            .Select(m => m.Groups[1].Value)
            .ToList();
        Assert.Contains("Customer", allowed);

        var backfill = Regex.Match(up, @"requested_by_party\s*=\s*CASE WHEN r\.requested_by_party = '' THEN '([^']+)'");
        Assert.True(backfill.Success, "The backfill must set requested_by_party explicitly.");
        Assert.Contains(backfill.Groups[1].Value, allowed);
    }

    [Fact]
    public void Unresolvable_reschedule_rows_are_removed_so_the_appointment_fk_holds()
    {
        var up = UpBody();

        // A reschedule request whose appointment is gone cannot satisfy the
        // cascade FK that this migration adds, so it is deleted deliberately
        // rather than left to abort the upgrade.
        Assert.Contains("DELETE FROM reschedule_requests AS r", up);
        Assert.Contains("WHERE NOT EXISTS (SELECT 1 FROM appointments AS a WHERE a.id = r.appointment_id)", up);
    }

    [Fact]
    public void The_migration_creates_every_business_operations_table()
    {
        var up = UpBody();

        foreach (var table in new[]
                 {
                     "service_categories", "staff_positions", "staff_position_history",
                     "commission_configs", "commission_entries",
                     "commission_payouts", "business_roles", "business_role_permissions",
                     "business_membership_role_assignments"
                 })
        {
            Assert.Contains($"name: \"{table}\"", up);
        }
    }

    [Fact]
    public void Monetary_columns_use_a_fixed_decimal_precision()
    {
        var up = UpBody();

        // Money is never a float: the commission ledger has to be exact.
        Assert.Contains("type: \"numeric(14,2)\"", up);
        Assert.Contains("type: \"numeric(7,4)\"", up);
        Assert.DoesNotContain("type: \"double precision\"", up);
        Assert.DoesNotContain("type: \"real\"", up);
    }

    [Fact]
    public void The_soft_deleted_category_and_position_names_are_unique_per_business()
    {
        var up = UpBody();

        Assert.Contains("ux_service_categories_business_name", up);
        Assert.Contains("ux_staff_positions_business_name", up);
        Assert.Contains("ux_business_roles_business_key", up);
    }
}
