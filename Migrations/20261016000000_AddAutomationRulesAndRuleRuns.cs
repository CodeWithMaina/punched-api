using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PunchedApi.Infrastructure.Data;

#nullable disable

namespace PunchedApi.Migrations;

/// <summary>
/// Phase 3: automation rules engine persistence.
/// Creates <c>automation_rules</c> and <c>automation_rule_runs</c>.
/// Additive only — no existing tables or columns are modified.
/// </summary>
[DbContext(typeof(ApplicationDbContext))]
[Migration("20261016000000_AddAutomationRulesAndRuleRuns")]
public partial class AddAutomationRulesAndRuleRuns : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "automation_rules",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                business_id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                trigger = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                conditions_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{}"),
                actions_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "[]"),
                recipients_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "[]"),
                enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                daily_action_cap = table.Column<int>(type: "integer", nullable: true),
                frequency_cap_minutes = table.Column<int>(type: "integer", nullable: true),
                quiet_hours_start = table.Column<TimeSpan>(type: "interval", nullable: true),
                quiet_hours_end = table.Column<TimeSpan>(type: "interval", nullable: true),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_automation_rules", x => x.id);
                table.ForeignKey(
                    name: "FK_automation_rules_businesses_business_id",
                    column: x => x.business_id,
                    principalTable: "businesses",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "automation_rule_runs",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                rule_id = table.Column<Guid>(type: "uuid", nullable: false),
                rule_version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                source_event_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                source_aggregate_id = table.Column<Guid>(type: "uuid", nullable: true),
                source_job_id = table.Column<Guid>(type: "uuid", nullable: true),
                status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "pending"),
                created_notification_intent_ids_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "[]"),
                created_job_ids_json = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "[]"),
                error_message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                error_details_json = table.Column<string>(type: "jsonb", nullable: true),
                idempotency_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                business_id = table.Column<Guid>(type: "uuid", nullable: false),
                started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_automation_rule_runs", x => x.id);
                table.ForeignKey(
                    name: "FK_automation_rule_runs_automation_rules_rule_id",
                    column: x => x.rule_id,
                    principalTable: "automation_rules",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_automation_rule_runs_businesses_business_id",
                    column: x => x.business_id,
                    principalTable: "businesses",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "ix_automation_rules_business_enabled",
            table: "automation_rules",
            columns: new[] { "business_id", "enabled" });

        migrationBuilder.CreateIndex(
            name: "ix_automation_rules_business_trigger",
            table: "automation_rules",
            columns: new[] { "business_id", "trigger" });

        migrationBuilder.CreateIndex(
            name: "ux_automation_rules_business_name",
            table: "automation_rules",
            columns: new[] { "business_id", "name" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_automation_rule_runs_business_created",
            table: "automation_rule_runs",
            columns: new[] { "business_id", "created_at" });

        migrationBuilder.CreateIndex(
            name: "ix_automation_rule_runs_business_status",
            table: "automation_rule_runs",
            columns: new[] { "business_id", "status", "created_at" });

        migrationBuilder.CreateIndex(
            name: "ix_automation_rule_runs_event_aggregate",
            table: "automation_rule_runs",
            columns: new[] { "source_event_type", "source_aggregate_id" });

        migrationBuilder.CreateIndex(
            name: "ix_automation_rule_runs_rule_version",
            table: "automation_rule_runs",
            columns: new[] { "rule_id", "rule_version", "created_at" });

        migrationBuilder.CreateIndex(
            name: "ux_automation_rule_runs_idempotency_key",
            table: "automation_rule_runs",
            column: "idempotency_key",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "automation_rule_runs");

        migrationBuilder.DropTable(
            name: "automation_rules");
    }
}