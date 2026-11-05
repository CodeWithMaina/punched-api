using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PunchedApi.Migrations
{
    /// <inheritdoc />
    public partial class AddAdminBusinessStatusAndSubscriptionPause : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "deactivated_at",
                table: "businesses",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "status",
                table: "businesses",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "active");

            migrationBuilder.AddColumn<DateTime>(
                name: "suspended_at",
                table: "businesses",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "suspended_reason",
                table: "businesses",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "current_period_starts_at",
                table: "business_subscriptions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pause_reason",
                table: "business_subscriptions",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "paused_at",
                table: "business_subscriptions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_businesses_status",
                table: "businesses",
                column: "status");

            migrationBuilder.AddCheckConstraint(
                name: "chk_business_status",
                table: "businesses",
                sql: "\"status\" IN ('active', 'suspended', 'deactivated')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_businesses_status",
                table: "businesses");

            migrationBuilder.DropCheckConstraint(
                name: "chk_business_status",
                table: "businesses");

            migrationBuilder.DropColumn(
                name: "deactivated_at",
                table: "businesses");

            migrationBuilder.DropColumn(
                name: "status",
                table: "businesses");

            migrationBuilder.DropColumn(
                name: "suspended_at",
                table: "businesses");

            migrationBuilder.DropColumn(
                name: "suspended_reason",
                table: "businesses");

            migrationBuilder.DropColumn(
                name: "current_period_starts_at",
                table: "business_subscriptions");

            migrationBuilder.DropColumn(
                name: "pause_reason",
                table: "business_subscriptions");

            migrationBuilder.DropColumn(
                name: "paused_at",
                table: "business_subscriptions");
        }
    }
}
