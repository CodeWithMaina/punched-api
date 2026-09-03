using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PunchedApi.Migrations
{
    /// <inheritdoc />
    public partial class AddSubscriptionAdminLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "archived_at",
                table: "subscription_plans",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "deactivated_at",
                table: "subscription_plans",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "display_order",
                table: "subscription_plans",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_default",
                table: "subscription_plans",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "last_published_by_user_id",
                table: "subscription_plans",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "lifecycle_state",
                table: "subscription_plans",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Draft");

            migrationBuilder.AddColumn<DateTime>(
                name: "published_at",
                table: "subscription_plans",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "subscription_audit_logs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    target_business_id = table.Column<Guid>(type: "uuid", nullable: true),
                    target_plan_id = table.Column<Guid>(type: "uuid", nullable: true),
                    payload_json = table.Column<string>(type: "text", nullable: true),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_subscription_audit_logs", x => x.id);
                    table.ForeignKey(
                        name: "FK_subscription_audit_logs_businesses_target_business_id",
                        column: x => x.target_business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_subscription_audit_logs_subscription_plans_target_plan_id",
                        column: x => x.target_plan_id,
                        principalTable: "subscription_plans",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_subscription_audit_logs_users_actor_user_id",
                        column: x => x.actor_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_subscription_plans_last_published_by_user_id",
                table: "subscription_plans",
                column: "last_published_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_subscription_plans_one_default",
                table: "subscription_plans",
                column: "is_default",
                unique: true,
                filter: "\"is_default\" = true");

            migrationBuilder.CreateIndex(
                name: "ix_subscription_audit_logs_actor_user_id",
                table: "subscription_audit_logs",
                column: "actor_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_subscription_audit_logs_created_at",
                table: "subscription_audit_logs",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_subscription_audit_logs_target_business_created",
                table: "subscription_audit_logs",
                columns: new[] { "target_business_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_subscription_audit_logs_target_plan_id",
                table: "subscription_audit_logs",
                column: "target_plan_id");

            migrationBuilder.AddForeignKey(
                name: "FK_subscription_plans_users_last_published_by_user_id",
                table: "subscription_plans",
                column: "last_published_by_user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            // ── Backfill existing plans (non-breaking) ──────────────
            // Map the legacy is_active flag onto the new lifecycle_state,
            // stamp published_at for active tiers, and designate the seeded
            // "starter" tier as the default (at most one default is enforced
            // by the unique partial index just created).
            migrationBuilder.Sql(
                """
                UPDATE "subscription_plans"
                SET "lifecycle_state" = CASE WHEN "is_active" THEN 'Active' ELSE 'Inactive' END,
                    "published_at"   = CASE WHEN "is_active" THEN COALESCE("published_at", "created_at") ELSE "published_at" END;
                """);

            migrationBuilder.Sql(
                """
                UPDATE "subscription_plans"
                SET "is_default" = true
                WHERE "key" = 'starter' AND "is_default" = false AND NOT EXISTS (
                    SELECT 1 FROM "subscription_plans" d WHERE d."is_default" = true
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_subscription_plans_users_last_published_by_user_id",
                table: "subscription_plans");

            migrationBuilder.DropTable(
                name: "subscription_audit_logs");

            migrationBuilder.DropIndex(
                name: "IX_subscription_plans_last_published_by_user_id",
                table: "subscription_plans");

            migrationBuilder.DropIndex(
                name: "ix_subscription_plans_one_default",
                table: "subscription_plans");

            migrationBuilder.DropColumn(
                name: "archived_at",
                table: "subscription_plans");

            migrationBuilder.DropColumn(
                name: "deactivated_at",
                table: "subscription_plans");

            migrationBuilder.DropColumn(
                name: "display_order",
                table: "subscription_plans");

            migrationBuilder.DropColumn(
                name: "is_default",
                table: "subscription_plans");

            migrationBuilder.DropColumn(
                name: "last_published_by_user_id",
                table: "subscription_plans");

            migrationBuilder.DropColumn(
                name: "lifecycle_state",
                table: "subscription_plans");

            migrationBuilder.DropColumn(
                name: "published_at",
                table: "subscription_plans");
        }
    }
}
