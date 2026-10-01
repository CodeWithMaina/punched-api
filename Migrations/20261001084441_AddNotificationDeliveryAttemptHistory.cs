using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PunchedApi.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationDeliveryAttemptHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "delivery_attempts_json",
                table: "notifications",
                type: "jsonb",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE notifications
                SET delivery_attempts_json = jsonb_build_object(
                    'version', 1,
                    'complete', TRUE,
                    'attempts', jsonb_build_array(jsonb_build_object(
                        'startedAtUtc', sent_at,
                        'completedAtUtc', sent_at,
                        'outcome', 'sent')))
                WHERE channel = 'in_app'
                  AND status = 'sent'
                  AND delivery_attempts_json IS NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "ix_notifications_failed_updated_id",
                table: "notifications",
                columns: new[] { "status", "updated_at", "id" },
                filter: "status = 'failed'");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_updated_at",
                table: "notifications",
                column: "updated_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_notifications_failed_updated_id",
                table: "notifications");

            migrationBuilder.DropIndex(
                name: "ix_notifications_updated_at",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "delivery_attempts_json",
                table: "notifications");
        }
    }
}
