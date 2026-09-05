using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PunchedApi.Migrations
{
    /// <inheritdoc />
    public partial class AddReferralLinkOpenTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "first_opened_at",
                table: "referral_links",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "last_opened_at",
                table: "referral_links",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "open_count",
                table: "referral_links",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddCheckConstraint(
                name: "chk_referral_link_open_count_gte_zero",
                table: "referral_links",
                sql: "\"open_count\" >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "chk_referral_link_open_count_gte_zero",
                table: "referral_links");

            migrationBuilder.DropColumn(
                name: "first_opened_at",
                table: "referral_links");

            migrationBuilder.DropColumn(
                name: "last_opened_at",
                table: "referral_links");

            migrationBuilder.DropColumn(
                name: "open_count",
                table: "referral_links");
        }
    }
}
