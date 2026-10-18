using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PunchedApi.Infrastructure.Data;

#nullable disable

namespace PunchedApi.Migrations;

/// <summary>
/// Phase 4: templates and branding persistence.
/// Creates <c>notification_template_overrides</c> and
/// <c>business_messaging_profiles</c>. Additive only.
/// </summary>
[DbContext(typeof(ApplicationDbContext))]
[Migration("20261017000000_AddTemplateOverridesAndMessagingProfiles")]
public partial class AddTemplateOverridesAndMessagingProfiles : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "business_messaging_profiles",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                business_id = table.Column<Guid>(type: "uuid", nullable: false),
                display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                logo_media_id = table.Column<Guid>(type: "uuid", nullable: true),
                primary_color = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                reply_to = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                footer_note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                sender_domain_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                timezone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                reminder_policy_json = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, defaultValue: "[1440,60]"),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_business_messaging_profiles", x => x.id);
                table.ForeignKey(
                    name: "FK_business_messaging_profiles_businesses_business_id",
                    column: x => x.business_id,
                    principalTable: "businesses",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "notification_template_overrides",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                business_id = table.Column<Guid>(type: "uuid", nullable: false),
                type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                channel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                subject_template = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                body_template_html = table.Column<string>(type: "text", nullable: false),
                body_template_text = table.Column<string>(type: "text", nullable: false),
                version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                updated_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_notification_template_overrides", x => x.id);
                table.ForeignKey(
                    name: "FK_notification_template_overrides_businesses_business_id",
                    column: x => x.business_id,
                    principalTable: "businesses",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "ux_business_messaging_profiles_business_id",
            table: "business_messaging_profiles",
            column: "business_id",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ux_notification_template_overrides_business_type_channel",
            table: "notification_template_overrides",
            columns: new[] { "business_id", "type", "channel" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_notification_template_overrides_business_type",
            table: "notification_template_overrides",
            columns: new[] { "business_id", "type" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "notification_template_overrides");

        migrationBuilder.DropTable(
            name: "business_messaging_profiles");
    }
}