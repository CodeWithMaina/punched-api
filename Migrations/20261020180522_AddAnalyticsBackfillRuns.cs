using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PunchedApi.Migrations
{
    /// <inheritdoc />
    public partial class AddAnalyticsBackfillRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "analytics_backfill_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: true),
                    from_date = table.Column<DateOnly>(type: "date", nullable: false),
                    to_date = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    last_completed_date = table.Column<DateOnly>(type: "date", nullable: true),
                    total_days = table.Column<int>(type: "integer", nullable: false),
                    completed_days = table.Column<int>(type: "integer", nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    last_progress_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_analytics_backfill_runs", x => x.id);
                    table.CheckConstraint("chk_backfill_range_valid", "\"to_date\" >= \"from_date\"");
                    table.CheckConstraint("chk_backfill_status_valid", "\"status\" IN ('Pending','Running','Completed','Failed','Cancelled')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_stamps_stamped_at",
                table: "stamps",
                column: "stamped_at");

            migrationBuilder.CreateIndex(
                name: "ix_redemptions_paid_at",
                table: "redemptions",
                column: "paid_at");

            migrationBuilder.CreateIndex(
                name: "ix_redemptions_redeemed_at",
                table: "redemptions",
                column: "redeemed_at");

            migrationBuilder.CreateIndex(
                name: "ix_loyalty_cards_enrolled_at",
                table: "loyalty_cards",
                column: "enrolled_at");

            migrationBuilder.CreateIndex(
                name: "ix_backfill_runs_business",
                table: "analytics_backfill_runs",
                column: "business_id");

            migrationBuilder.CreateIndex(
                name: "ix_backfill_runs_group_business",
                table: "analytics_backfill_runs",
                columns: new[] { "run_group_id", "business_id" });

            migrationBuilder.CreateIndex(
                name: "ix_backfill_runs_status",
                table: "analytics_backfill_runs",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "analytics_backfill_runs");

            migrationBuilder.DropIndex(
                name: "ix_stamps_stamped_at",
                table: "stamps");

            migrationBuilder.DropIndex(
                name: "ix_redemptions_paid_at",
                table: "redemptions");

            migrationBuilder.DropIndex(
                name: "ix_redemptions_redeemed_at",
                table: "redemptions");

            migrationBuilder.DropIndex(
                name: "ix_loyalty_cards_enrolled_at",
                table: "loyalty_cards");
        }
    }
}
