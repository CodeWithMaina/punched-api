using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PunchedApi.Migrations
{
    /// <inheritdoc />
    public partial class StaffOnboardingAndAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "invited_full_name",
                table: "staff_invitations",
                type: "character varying(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "invited_phone_number",
                table: "staff_invitations",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "position_id",
                table: "staff_invitations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "service_category_ids_json",
                table: "staff_invitations",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "staff_audit_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    staff_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    event_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    summary = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    details_json = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_staff_audit_events", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_staff_audit_events_business_created",
                table: "staff_audit_events",
                columns: new[] { "business_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_staff_audit_events_business_staff_created",
                table: "staff_audit_events",
                columns: new[] { "business_id", "staff_user_id", "created_at" },
                descending: new[] { false, false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "staff_audit_events");

            migrationBuilder.DropColumn(
                name: "invited_full_name",
                table: "staff_invitations");

            migrationBuilder.DropColumn(
                name: "invited_phone_number",
                table: "staff_invitations");

            migrationBuilder.DropColumn(
                name: "position_id",
                table: "staff_invitations");

            migrationBuilder.DropColumn(
                name: "service_category_ids_json",
                table: "staff_invitations");
        }
    }
}
