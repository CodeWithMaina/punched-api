using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PunchedApi.Migrations
{
    /// <summary>
    /// Repairs databases where stamps.unlocked_at was never actually created
    /// despite the migration being recorded in __EFMigrationsHistory.
    /// The 20260904100000_AddStampUnlockedAt migration shipped without a
    /// Designer.cs, and the subsequent EnsureStampUnlockedAtColumn fix
    /// migration was recorded as applied but the ALTER TABLE never took
    /// effect on the target database.
    /// </summary>
    public partial class FixStampUnlockedAtColumn : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                @"ALTER TABLE stamps ADD COLUMN IF NOT EXISTS unlocked_at timestamp with time zone NULL;");

            migrationBuilder.Sql(
                @"UPDATE stamps SET unlocked_at = stamped_at WHERE unlocked_at IS NULL;");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                @"ALTER TABLE stamps DROP COLUMN IF EXISTS unlocked_at;");
        }
    }
}
