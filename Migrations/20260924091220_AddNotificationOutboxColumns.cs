using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PunchedApi.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationOutboxColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "attempts",
                table: "notifications",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "idempotency_key",
                table: "notifications",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "next_attempt_at",
                table: "notifications",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<string>(
                name: "payload_json",
                table: "notifications",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<DateTime>(
                name: "updated_at",
                table: "notifications",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_outbox_claim",
                table: "notifications",
                columns: new[] { "status", "next_attempt_at", "created_at" },
                filter: "status = 'pending'");

            migrationBuilder.CreateIndex(
                name: "ux_notifications_idempotency",
                table: "notifications",
                column: "idempotency_key",
                unique: true,
                filter: "idempotency_key IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_notifications_outbox_claim",
                table: "notifications");

            migrationBuilder.DropIndex(
                name: "ux_notifications_idempotency",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "attempts",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "idempotency_key",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "next_attempt_at",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "payload_json",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "updated_at",
                table: "notifications");
        }
    }
}
