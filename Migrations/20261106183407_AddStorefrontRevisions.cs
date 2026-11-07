using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PunchedApi.Migrations
{
    /// <inheritdoc />
    public partial class AddStorefrontRevisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "storefront_revisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision_number = table.Column<int>(type: "integer", nullable: false),
                    configuration_json = table.Column<string>(type: "text", nullable: false),
                    revision_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source_revision_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_published_snapshot = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_storefront_revisions", x => x.id);
                    table.CheckConstraint("ck_storefront_revisions_number_positive", "\"revision_number\" >= 1");
                    table.ForeignKey(
                        name: "FK_storefront_revisions_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_storefront_revisions_storefront_revisions_source_revision_id",
                        column: x => x.source_revision_id,
                        principalTable: "storefront_revisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_storefront_revisions_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_storefront_revisions_business_created",
                table: "storefront_revisions",
                columns: new[] { "business_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_storefront_revisions_business_type",
                table: "storefront_revisions",
                columns: new[] { "business_id", "revision_type" });

            migrationBuilder.CreateIndex(
                name: "ix_storefront_revisions_created_by",
                table: "storefront_revisions",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_storefront_revisions_source",
                table: "storefront_revisions",
                column: "source_revision_id");

            migrationBuilder.CreateIndex(
                name: "ux_storefront_revisions_business_number",
                table: "storefront_revisions",
                columns: new[] { "business_id", "revision_number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "storefront_revisions");
        }
    }
}
