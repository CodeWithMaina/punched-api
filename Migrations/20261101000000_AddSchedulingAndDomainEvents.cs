using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PunchedApi.Infrastructure.Data;

#nullable disable

namespace PunchedApi.Migrations
{
    /// <summary>
    /// Phase 2 scheduling core: scheduled_jobs (dedupe-keyed reminder/follow-up
    /// jobs), domain_events (durable relay with per-handler checkpoints),
    /// business_reminder_policies, and the per-appointment reminder opt-out flag.
    /// Hand-written additive migration (repo convention for Phase 1-2 changes);
    /// the model snapshot is updated in step with it.
    /// </summary>
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20261101000000_AddSchedulingAndDomainEvents")]
    public partial class AddSchedulingAndDomainEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "scheduled_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    dedupe_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: true),
                    aggregate_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    aggregate_id = table.Column<Guid>(type: "uuid", nullable: true),
                    payload_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    scheduled_for_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "pending"),
                    attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    locked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    cancelled_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scheduled_jobs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "domain_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    aggregate_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    aggregate_id = table.Column<Guid>(type: "uuid", nullable: true),
                    business_id = table.Column<Guid>(type: "uuid", nullable: true),
                    payload_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                    occurred_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "pending"),
                    attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    handled_by_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "[]"),
                    locked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_domain_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "business_reminder_policies",
                columns: table => new
                {
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    offset_minutes_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "[1440,60]"),
                    channels_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "[\"in_app\",\"email\",\"push\"]"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_business_reminder_policies", x => x.business_id);
                    table.ForeignKey(
                        name: "FK_business_reminder_policies_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_scheduled_jobs_dedupe_key",
                table: "scheduled_jobs",
                column: "dedupe_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_scheduled_jobs_status_due",
                table: "scheduled_jobs",
                columns: new[] { "status", "scheduled_for_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_scheduled_jobs_aggregate",
                table: "scheduled_jobs",
                columns: new[] { "aggregate_type", "aggregate_id" });

            migrationBuilder.CreateIndex(
                name: "ix_domain_events_status_occurred",
                table: "domain_events",
                columns: new[] { "status", "occurred_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_domain_events_aggregate",
                table: "domain_events",
                columns: new[] { "aggregate_type", "aggregate_id" });

            migrationBuilder.CreateIndex(
                name: "ix_domain_events_event_type",
                table: "domain_events",
                column: "event_type");

            migrationBuilder.AddColumn<bool>(
                name: "reminder_opt_out",
                table: "appointments",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "reminder_opt_out",
                table: "appointments");

            migrationBuilder.DropTable(
                name: "business_reminder_policies");

            migrationBuilder.DropTable(
                name: "domain_events");

            migrationBuilder.DropTable(
                name: "scheduled_jobs");
        }
    }
}