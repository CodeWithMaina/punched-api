using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PunchedApi.Migrations
{
    /// <inheritdoc />
    public partial class AddStorefrontBuilderConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "draft_config_json",
                table: "business_landing_page_configs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_published",
                table: "business_landing_page_configs",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "published_at",
                table: "business_landing_page_configs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "published_config_json",
                table: "business_landing_page_configs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "published_version",
                table: "business_landing_page_configs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "template_id",
                table: "business_landing_page_configs",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "draft_config_json",
                table: "business_landing_page_configs");

            migrationBuilder.DropColumn(
                name: "is_published",
                table: "business_landing_page_configs");

            migrationBuilder.DropColumn(
                name: "published_at",
                table: "business_landing_page_configs");

            migrationBuilder.DropColumn(
                name: "published_config_json",
                table: "business_landing_page_configs");

            migrationBuilder.DropColumn(
                name: "published_version",
                table: "business_landing_page_configs");

            migrationBuilder.DropColumn(
                name: "template_id",
                table: "business_landing_page_configs");
        }
    }
}
