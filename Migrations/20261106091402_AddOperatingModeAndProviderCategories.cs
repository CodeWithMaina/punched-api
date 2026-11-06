using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PunchedApi.Migrations
{
    /// <inheritdoc />
    public partial class AddOperatingModeAndProviderCategories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "operating_mode",
                table: "businesses",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "multi_staff");

            migrationBuilder.CreateTable(
                name: "staff_service_categories",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    staff_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_category_id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_staff_service_categories", x => x.id);
                    table.ForeignKey(
                        name: "FK_staff_service_categories_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_staff_service_categories_service_categories_service_categor~",
                        column: x => x.service_category_id,
                        principalTable: "service_categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_staff_service_categories_users_staff_user_id",
                        column: x => x.staff_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "chk_business_operating_mode",
                table: "businesses",
                sql: "\"operating_mode\" IN ('solo', 'multi_staff')");

            migrationBuilder.CreateIndex(
                name: "ix_staff_service_categories_business_category",
                table: "staff_service_categories",
                columns: new[] { "business_id", "service_category_id" });

            migrationBuilder.CreateIndex(
                name: "ix_staff_service_categories_business_staff",
                table: "staff_service_categories",
                columns: new[] { "business_id", "staff_user_id" });

            migrationBuilder.CreateIndex(
                name: "IX_staff_service_categories_service_category_id",
                table: "staff_service_categories",
                column: "service_category_id");

            migrationBuilder.CreateIndex(
                name: "IX_staff_service_categories_staff_user_id",
                table: "staff_service_categories",
                column: "staff_user_id");

            migrationBuilder.CreateIndex(
                name: "ux_staff_service_categories_business_staff_category",
                table: "staff_service_categories",
                columns: new[] { "business_id", "staff_user_id", "service_category_id" },
                unique: true);

            // ── Safe data backfill ────────────────────────────────────────────
            // Associate existing staff with the categories their EXISTING explicit
            // service assignments already imply. Only categorised services are
            // used, so no staff member is filed under a guessed category; services
            // that are still uncategorised keep working through the legacy
            // staff_services fallback. Idempotent via the NOT EXISTS guard.
            migrationBuilder.Sql(@"
INSERT INTO staff_service_categories (id, staff_user_id, service_category_id, business_id, created_at)
SELECT DISTINCT
    gen_random_uuid(),
    ss.staff_user_id,
    s.category_id,
    ss.business_id,
    NOW() AT TIME ZONE 'UTC'
FROM staff_services ss
JOIN services s ON s.id = ss.service_id
WHERE s.category_id IS NOT NULL
  AND ss.business_id = s.business_id
  AND NOT EXISTS (
      SELECT 1 FROM staff_service_categories c
      WHERE c.business_id = ss.business_id
        AND c.staff_user_id = ss.staff_user_id
        AND c.service_category_id = s.category_id
  );
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "staff_service_categories");

            migrationBuilder.DropCheckConstraint(
                name: "chk_business_operating_mode",
                table: "businesses");

            migrationBuilder.DropColumn(
                name: "operating_mode",
                table: "businesses");
        }
    }
}
