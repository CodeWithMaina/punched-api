using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PunchedApi.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationPreferencesAndInboxPayload : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "archived_at",
                table: "notification_inbox",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "payload_json",
                table: "notification_inbox",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.CreateTable(
                name: "notification_preferences",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    business_id = table.Column<Guid>(type: "uuid", nullable: true),
                    category = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    channel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_preferences", x => x.id);
                    table.CheckConstraint("ck_notification_preferences_scope", "\"user_id\" IS NOT NULL OR \"business_id\" IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_notification_preferences_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_notification_preferences_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql("CREATE INDEX ix_notification_inbox_user_unread ON notification_inbox (user_id, is_read, created_at DESC) WHERE is_read = FALSE;");

            migrationBuilder.CreateIndex(
                name: "ix_notification_prefs_lookup",
                table: "notification_preferences",
                columns: new[] { "user_id", "business_id", "category" });

            migrationBuilder.CreateIndex(
                name: "ux_notification_prefs_business",
                table: "notification_preferences",
                columns: new[] { "business_id", "category", "channel" },
                unique: true,
                filter: "\"user_id\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_notification_prefs_user_business",
                table: "notification_preferences",
                columns: new[] { "user_id", "business_id", "category", "channel" },
                unique: true,
                filter: "\"business_id\" IS NOT NULL AND \"user_id\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_notification_prefs_user_global",
                table: "notification_preferences",
                columns: new[] { "user_id", "category", "channel" },
                unique: true,
                filter: "\"business_id\" IS NULL AND \"user_id\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notification_preferences");

            migrationBuilder.DropIndex(
                name: "ix_notification_inbox_user_unread",
                table: "notification_inbox");

            migrationBuilder.DropColumn(
                name: "archived_at",
                table: "notification_inbox");

            migrationBuilder.DropColumn(
                name: "payload_json",
                table: "notification_inbox");
        }
    }
}
