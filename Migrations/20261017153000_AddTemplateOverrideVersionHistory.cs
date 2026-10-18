using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PunchedApi.Infrastructure.Data;

#nullable disable

namespace PunchedApi.Migrations;

/// <summary>
/// Phase 2: durable template override version history.
/// Adds <c>updated_at</c> to <c>notification_template_overrides</c> and creates
/// <c>notification_template_override_versions</c> for restore-as-new-version.
/// Additive only — does not touch existing tables beyond one new nullable column.
/// </summary>
[DbContext(typeof(ApplicationDbContext))]
[Migration("20261017153000_AddTemplateOverrideVersionHistory")]
public partial class AddTemplateOverrideVersionHistory : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTime>(
            name: "updated_at",
            table: "notification_template_overrides",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "notification_template_override_versions",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                override_id = table.Column<Guid>(type: "uuid", nullable: false),
                business_id = table.Column<Guid>(type: "uuid", nullable: false),
                type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                channel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                subject_template = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                body_template_html = table.Column<string>(type: "text", nullable: false),
                body_template_text = table.Column<string>(type: "text", nullable: false),
                version = table.Column<int>(type: "integer", nullable: false),
                updated_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_notification_template_override_versions", x => x.id);
                table.ForeignKey(
                    name: "FK_notification_template_override_versions_notification_template_overrides_override_id",
                    column: x => x.override_id,
                    principalTable: "notification_template_overrides",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "ux_notification_template_override_versions_override_version",
            table: "notification_template_override_versions",
            columns: new[] { "override_id", "version" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_notification_template_override_versions_business_type_channel",
            table: "notification_template_override_versions",
            columns: new[] { "business_id", "type", "channel" });

        migrationBuilder.CreateIndex(
            name: "ix_notification_template_override_versions_override_id",
            table: "notification_template_override_versions",
            column: "override_id");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "notification_template_override_versions");

        migrationBuilder.DropColumn(
            name: "updated_at",
            table: "notification_template_overrides");
    }
}
