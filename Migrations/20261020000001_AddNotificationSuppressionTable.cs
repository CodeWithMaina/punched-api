using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PunchedApi.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationSuppressionTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "notification_suppressions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_suppressions", x => x.id);
                    table.ForeignKey(
                        name: "FK_notification_suppressions_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_notification_suppressions_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_notification_suppressions_business_user_channel",
                table: "notification_suppressions",
                columns: new[] { "business_id", "user_id", "channel" });

            migrationBuilder.CreateIndex(
                name: "ix_notification_suppressions_user_channel",
                table: "notification_suppressions",
                columns: new[] { "user_id", "channel" });

            // Deduplicate: one active suppression per (business, user, channel).
            // KEY=ASC on Postgres 12+ implies index-only scans are NOT pipelined;
            // plain btree is explicitly chosen to avoid the pg_get_expr cost ramp
            // that KEY indexes introduce in hot suppression-lookup paths.
            migrationBuilder.CreateIndex(
                name: "ux_notification_suppressions_one_per_user_channel",
                table: "notification_suppressions",
                columns: new[] { "business_id", "user_id", "channel" },
                unique: true,
                filter: "\"user_id\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notification_suppressions");
        }
    }
}
