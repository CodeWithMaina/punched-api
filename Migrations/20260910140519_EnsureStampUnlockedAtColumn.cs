using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PunchedApi.Migrations
{
    /// <inheritdoc />
    public partial class EnsureStampUnlockedAtColumn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Idempotent repair: the 20260904100000_AddStampUnlockedAt migration
            // shipped without its Designer, so some databases never received
            // stamps.unlocked_at while the model snapshot expects it
            // (GET /v1/cards -> 42703: column s.unlocked_at does not exist).
            migrationBuilder.Sql(
                "ALTER TABLE stamps ADD COLUMN IF NOT EXISTS unlocked_at timestamp with time zone NULL;");
            migrationBuilder.Sql(
                "UPDATE stamps SET unlocked_at = stamped_at WHERE unlocked_at IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER TABLE stamps DROP COLUMN IF EXISTS unlocked_at;");
        }
    }
}
