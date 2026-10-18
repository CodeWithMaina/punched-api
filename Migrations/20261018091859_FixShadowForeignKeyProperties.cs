using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PunchedApi.Migrations
{
    /// <inheritdoc />
    public partial class FixShadowForeignKeyProperties : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty. The BusinessId1/RuleId1/OverrideId1 shadow foreign-key
            // properties were an EF model-only artifact: the [ForeignKey] attribute on each
            // entity's navigation property already claimed BusinessId/RuleId/OverrideId, and
            // the generic HasOne<T>() configuration overload built a *second* relationship,
            // forcing EF to invent shadow "…1" columns. These shadow columns were never
            // created in the database (the original 20261016000000 migration only created
            // business_id / rule_id), so there is nothing to drop. The real fix was switching
            // each configuration to the navigation-property lambda (HasOne(x => x.Business));
            // this migration exists only to bring the model snapshot in line with the corrected
            // model so future scaffolds do not re-introduce the phantom columns.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "BusinessId1",
                table: "notification_template_overrides",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OverrideId1",
                table: "notification_template_override_versions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BusinessId1",
                table: "business_messaging_profiles",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BusinessId1",
                table: "automation_rules",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BusinessId1",
                table: "automation_rule_runs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RuleId1",
                table: "automation_rule_runs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_notification_template_overrides_BusinessId1",
                table: "notification_template_overrides",
                column: "BusinessId1");

            migrationBuilder.CreateIndex(
                name: "IX_notification_template_override_versions_OverrideId1",
                table: "notification_template_override_versions",
                column: "OverrideId1");

            migrationBuilder.CreateIndex(
                name: "IX_business_messaging_profiles_BusinessId1",
                table: "business_messaging_profiles",
                column: "BusinessId1");

            migrationBuilder.CreateIndex(
                name: "IX_automation_rules_BusinessId1",
                table: "automation_rules",
                column: "BusinessId1");

            migrationBuilder.CreateIndex(
                name: "IX_automation_rule_runs_BusinessId1",
                table: "automation_rule_runs",
                column: "BusinessId1");

            migrationBuilder.CreateIndex(
                name: "IX_automation_rule_runs_RuleId1",
                table: "automation_rule_runs",
                column: "RuleId1");

            migrationBuilder.AddForeignKey(
                name: "FK_automation_rule_runs_automation_rules_RuleId1",
                table: "automation_rule_runs",
                column: "RuleId1",
                principalTable: "automation_rules",
                principalColumn: "id");

            migrationBuilder.AddForeignKey(
                name: "FK_automation_rule_runs_businesses_BusinessId1",
                table: "automation_rule_runs",
                column: "BusinessId1",
                principalTable: "businesses",
                principalColumn: "id");

            migrationBuilder.AddForeignKey(
                name: "FK_automation_rules_businesses_BusinessId1",
                table: "automation_rules",
                column: "BusinessId1",
                principalTable: "businesses",
                principalColumn: "id");

            migrationBuilder.AddForeignKey(
                name: "FK_business_messaging_profiles_businesses_BusinessId1",
                table: "business_messaging_profiles",
                column: "BusinessId1",
                principalTable: "businesses",
                principalColumn: "id");

            migrationBuilder.AddForeignKey(
                name: "FK_notification_template_override_versions_notification_templ~1",
                table: "notification_template_override_versions",
                column: "OverrideId1",
                principalTable: "notification_template_overrides",
                principalColumn: "id");

            migrationBuilder.AddForeignKey(
                name: "FK_notification_template_overrides_businesses_BusinessId1",
                table: "notification_template_overrides",
                column: "BusinessId1",
                principalTable: "businesses",
                principalColumn: "id");
        }
    }
}
