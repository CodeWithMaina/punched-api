using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PunchedApi.Infrastructure.Data;

#nullable disable

namespace PunchedApi.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20261020000000_AddNotificationDeliverySchedulingColumns")]
    public partial class AddNotificationDeliverySchedulingColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Antiforgery / deterministic scheduling columns on the outbox ledger.
            // DeliverAfterUtc == 0 => invoice now; RuleId / SourceJobId link an intent to a rule
            // execution or a durable scheduled job; DeadLetteredAt + DeadLetterReason mark a permanent
            // terminal state distinct from 'failed'; TemplateKey pin the rendered template used.
            migrationBuilder.AddColumn<DateTime>(
                name: "deliver_after_utc",
                table: "notifications",
                type: "timestamp with time zone",
                nullable: true,
                defaultValueSql: "NULL");

            migrationBuilder.AddColumn<string>(
                name: "rule_id",
                table: "notifications",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_job_id",
                table: "notifications",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "dead_lettered_at",
                table: "notifications",
                type: "timestamp with time zone",
                nullable: true,
                defaultValueSql: "NULL");

            migrationBuilder.AddColumn<string>(
                name: "dead_letter_reason",
                table: "notifications",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "template_key",
                table: "notifications",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            // Dead-letter is now a distinct terminal state; ordering for pending claims stays
            // (status, next_attempt_at, created_at) — 'dead_lettered' sorts after 'pending'.
            migrationBuilder.CreateIndex(
                name: "ix_notifications_deliver_after_status",
                table: "notifications",
                columns: new[] { "deliver_after_utc", "status", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_notifications_deliver_after_status",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "deliver_after_utc",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "rule_id",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "source_job_id",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "dead_lettered_at",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "dead_letter_reason",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "template_key",
                table: "notifications");
        }
    }
}
