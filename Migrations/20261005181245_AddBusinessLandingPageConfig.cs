using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PunchedApi.Migrations
{
    /// <inheritdoc />
    public partial class AddBusinessLandingPageConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "business_landing_page_configs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    hero_background_media_id = table.Column<Guid>(type: "uuid", nullable: true),
                    config_json = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_business_landing_page_configs", x => x.id);
                    table.ForeignKey(
                        name: "FK_business_landing_page_configs_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_business_landing_page_configs_media_hero_background_media_id",
                        column: x => x.hero_background_media_id,
                        principalTable: "media",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_business_landing_page_configs_hero_background_media_id",
                table: "business_landing_page_configs",
                column: "hero_background_media_id");

            migrationBuilder.CreateIndex(
                name: "ux_business_landing_page_configs_business",
                table: "business_landing_page_configs",
                column: "business_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "business_landing_page_configs");
        }
    }
}
