using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PunchedApi.Migrations
{
    /// <inheritdoc />
    public partial class AddCardAssetAndDesignVersioning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "rules_version",
                table: "stamp_cards",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<DateTime>(
                name: "updated_at",
                table: "stamp_cards",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "required_stamps",
                table: "loyalty_cards",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "rules_version",
                table: "loyalty_cards",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "stamp_card_id",
                table: "loyalty_cards",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "config_json",
                table: "card_designs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "current_version",
                table: "card_designs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "updated_at",
                table: "card_designs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "card_assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    uploaded_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    purpose = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false, defaultValue: "ARTWORK"),
                    kind = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    content_type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    file_extension = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    size_bytes = table.Column<long>(type: "bigint", nullable: false),
                    width = table.Column<int>(type: "integer", nullable: false),
                    height = table.Column<int>(type: "integer", nullable: false),
                    storage_key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    original_file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_card_assets", x => x.id);
                    table.CheckConstraint("ck_card_assets_deleted_at_consistent", "(\"status\" = 0 AND \"deleted_at\" IS NULL) OR (\"status\" = 1 AND \"deleted_at\" IS NOT NULL)");
                    table.CheckConstraint("ck_card_assets_height_positive", "\"height\" > 0");
                    table.CheckConstraint("ck_card_assets_size_positive", "\"size_bytes\" > 0");
                    table.CheckConstraint("ck_card_assets_width_positive", "\"width\" > 0");
                    table.ForeignKey(
                        name: "FK_card_assets_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_card_assets_users_uploaded_by_user_id",
                        column: x => x.uploaded_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "card_design_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    card_design_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_number = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    html_template = table.Column<string>(type: "character varying(50000)", maxLength: 50000, nullable: false),
                    config_json = table.Column<string>(type: "text", nullable: true),
                    published_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    published_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    change_note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_card_design_versions", x => x.id);
                    table.CheckConstraint("ck_card_design_versions_html_length", "length(\"html_template\") <= 50000");
                    table.CheckConstraint("ck_card_design_versions_number_positive", "\"version_number\" >= 1");
                    table.ForeignKey(
                        name: "FK_card_design_versions_card_designs_card_design_id",
                        column: x => x.card_design_id,
                        principalTable: "card_designs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "stamp_card_rules_changes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    stamp_card_id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    changed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    changed_by_role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    field = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    old_value = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    new_value = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    applied_to_existing_cards = table.Column<bool>(type: "boolean", nullable: false),
                    affected_cards = table.Column<int>(type: "integer", nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    rules_version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stamp_card_rules_changes", x => x.id);
                    table.CheckConstraint("ck_stamp_card_rules_changes_affected_non_negative", "\"affected_cards\" >= 0");
                    table.CheckConstraint("ck_stamp_card_rules_changes_version_positive", "\"rules_version\" >= 1");
                    table.ForeignKey(
                        name: "FK_stamp_card_rules_changes_stamp_cards_stamp_card_id",
                        column: x => x.stamp_card_id,
                        principalTable: "stamp_cards",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "chk_stamp_card_reward_value_non_negative",
                table: "stamp_cards",
                sql: "\"reward_value\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "chk_stamp_card_rules_version_positive",
                table: "stamp_cards",
                sql: "\"rules_version\" >= 1");

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_cards_stamp_card_id",
                table: "loyalty_cards",
                column: "stamp_card_id");

            migrationBuilder.AddCheckConstraint(
                name: "chk_required_stamps_range",
                table: "loyalty_cards",
                sql: "\"required_stamps\" >= 0 AND \"required_stamps\" <= 100");

            migrationBuilder.AddCheckConstraint(
                name: "chk_rules_version_non_negative",
                table: "loyalty_cards",
                sql: "\"rules_version\" >= 0");

            migrationBuilder.CreateIndex(
                name: "ix_card_assets_business_created",
                table: "card_assets",
                columns: new[] { "business_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_card_assets_business_purpose_status",
                table: "card_assets",
                columns: new[] { "business_id", "purpose", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_card_assets_uploaded_by_user_id",
                table: "card_assets",
                column: "uploaded_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ux_card_assets_storage_key",
                table: "card_assets",
                column: "storage_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_card_design_versions_design_number",
                table: "card_design_versions",
                columns: new[] { "card_design_id", "version_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_stamp_card_rules_changes_business_created",
                table: "stamp_card_rules_changes",
                columns: new[] { "business_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_stamp_card_rules_changes_card_created",
                table: "stamp_card_rules_changes",
                columns: new[] { "stamp_card_id", "created_at" });

            migrationBuilder.AddForeignKey(
                name: "FK_loyalty_cards_stamp_cards_stamp_card_id",
                table: "loyalty_cards",
                column: "stamp_card_id",
                principalTable: "stamp_cards",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_loyalty_cards_stamp_cards_stamp_card_id",
                table: "loyalty_cards");

            migrationBuilder.DropTable(
                name: "card_assets");

            migrationBuilder.DropTable(
                name: "card_design_versions");

            migrationBuilder.DropTable(
                name: "stamp_card_rules_changes");

            migrationBuilder.DropCheckConstraint(
                name: "chk_stamp_card_reward_value_non_negative",
                table: "stamp_cards");

            migrationBuilder.DropCheckConstraint(
                name: "chk_stamp_card_rules_version_positive",
                table: "stamp_cards");

            migrationBuilder.DropIndex(
                name: "IX_loyalty_cards_stamp_card_id",
                table: "loyalty_cards");

            migrationBuilder.DropCheckConstraint(
                name: "chk_required_stamps_range",
                table: "loyalty_cards");

            migrationBuilder.DropCheckConstraint(
                name: "chk_rules_version_non_negative",
                table: "loyalty_cards");

            migrationBuilder.DropColumn(
                name: "rules_version",
                table: "stamp_cards");

            migrationBuilder.DropColumn(
                name: "updated_at",
                table: "stamp_cards");

            migrationBuilder.DropColumn(
                name: "required_stamps",
                table: "loyalty_cards");

            migrationBuilder.DropColumn(
                name: "rules_version",
                table: "loyalty_cards");

            migrationBuilder.DropColumn(
                name: "stamp_card_id",
                table: "loyalty_cards");

            migrationBuilder.DropColumn(
                name: "config_json",
                table: "card_designs");

            migrationBuilder.DropColumn(
                name: "current_version",
                table: "card_designs");

            migrationBuilder.DropColumn(
                name: "updated_at",
                table: "card_designs");
        }
    }
}
