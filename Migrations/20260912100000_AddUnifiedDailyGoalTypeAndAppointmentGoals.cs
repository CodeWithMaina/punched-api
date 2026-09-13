using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PunchedApi.Migrations
{
    /// <inheritdoc />
    public partial class AddUnifiedDailyGoalTypeAndAppointmentGoals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Business: active daily-goal type (stamps | appointments) + appointment default.
            migrationBuilder.Sql(
                "ALTER TABLE businesses ADD COLUMN IF NOT EXISTS daily_goal_type character varying(20) NOT NULL DEFAULT 'stamps';");
            migrationBuilder.Sql(
                "ALTER TABLE businesses ADD COLUMN IF NOT EXISTS default_appointment_daily_goal integer NULL;");

            // User: personal appointment-daily-goal override.
            migrationBuilder.Sql(
                "ALTER TABLE users ADD COLUMN IF NOT EXISTS appointment_daily_goal_override integer NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER TABLE businesses DROP COLUMN IF EXISTS daily_goal_type;");
            migrationBuilder.Sql(
                "ALTER TABLE businesses DROP COLUMN IF EXISTS default_appointment_daily_goal;");
            migrationBuilder.Sql(
                "ALTER TABLE users DROP COLUMN IF EXISTS appointment_daily_goal_override;");
        }
    }
}