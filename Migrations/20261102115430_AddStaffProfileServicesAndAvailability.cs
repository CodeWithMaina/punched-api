using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PunchedApi.Migrations
{
    /// <inheritdoc />
    public partial class AddStaffProfileServicesAndAvailability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "break_end_minute",
                table: "staff_shifts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "break_start_minute",
                table: "staff_shifts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "end_minute",
                table: "staff_shifts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "note",
                table: "staff_shifts",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "start_minute",
                table: "staff_shifts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "bio",
                table: "business_memberships",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_bookable",
                table: "business_memberships",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_public",
                table: "business_memberships",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            // Data-preserving backfill: existing rosters carry hour precision only.
            // Materialising it into the minute columns gives new readers one
            // authoritative representation without changing a single window that
            // already existed. Rows whose hours are not a real window (a day off, or
            // an unset end) are deliberately left NULL, because the reader falls back
            // to the hour columns for exactly those rows.
            migrationBuilder.Sql(
                "UPDATE staff_shifts SET start_minute = start_hour * 60, end_minute = end_hour * 60 " +
                "WHERE end_hour > start_hour AND (start_minute IS NULL OR end_minute IS NULL);");

            migrationBuilder.CreateTable(
                name: "staff_availability",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    staff_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day_of_week = table.Column<int>(type: "integer", nullable: false),
                    is_working = table.Column<bool>(type: "boolean", nullable: false),
                    start_minute = table.Column<int>(type: "integer", nullable: false),
                    end_minute = table.Column<int>(type: "integer", nullable: false),
                    break_start_minute = table.Column<int>(type: "integer", nullable: true),
                    break_end_minute = table.Column<int>(type: "integer", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_staff_availability", x => x.id);
                    table.CheckConstraint("chk_staff_availability_day_of_week", "\"day_of_week\" >= 0 AND \"day_of_week\" <= 6");
                    table.CheckConstraint("chk_staff_availability_minutes", "\"start_minute\" >= 0 AND \"start_minute\" <= 1439 AND \"end_minute\" >= 1 AND \"end_minute\" <= 1440 AND (\"is_working\" = false OR \"end_minute\" > \"start_minute\") AND (\"break_start_minute\" IS NULL OR (\"break_start_minute\" >= \"start_minute\" AND \"break_start_minute\" <= 1439)) AND (\"break_end_minute\" IS NULL OR (\"break_end_minute\" <= \"end_minute\" AND \"break_end_minute\" >= 1)) AND (\"break_start_minute\" IS NULL OR \"break_end_minute\" IS NULL OR \"break_end_minute\" > \"break_start_minute\")");
                    table.ForeignKey(
                        name: "FK_staff_availability_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_staff_availability_users_staff_user_id",
                        column: x => x.staff_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "chk_staff_shift_minutes",
                table: "staff_shifts",
                sql: "(\"start_minute\" IS NULL OR (\"start_minute\" >= 0 AND \"start_minute\" <= 1439)) AND (\"end_minute\" IS NULL OR (\"end_minute\" >= 1 AND \"end_minute\" <= 1440)) AND (\"break_start_minute\" IS NULL OR (\"break_start_minute\" >= 0 AND \"break_start_minute\" <= 1439)) AND (\"break_end_minute\" IS NULL OR (\"break_end_minute\" >= 1 AND \"break_end_minute\" <= 1440))");

            migrationBuilder.CreateIndex(
                name: "ix_staff_availability_business_staff",
                table: "staff_availability",
                columns: new[] { "business_id", "staff_user_id" });

            migrationBuilder.CreateIndex(
                name: "ux_staff_availability_staff_day",
                table: "staff_availability",
                columns: new[] { "staff_user_id", "day_of_week" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "staff_availability");

            migrationBuilder.DropCheckConstraint(
                name: "chk_staff_shift_minutes",
                table: "staff_shifts");

            migrationBuilder.DropColumn(
                name: "break_end_minute",
                table: "staff_shifts");

            migrationBuilder.DropColumn(
                name: "break_start_minute",
                table: "staff_shifts");

            migrationBuilder.DropColumn(
                name: "end_minute",
                table: "staff_shifts");

            migrationBuilder.DropColumn(
                name: "note",
                table: "staff_shifts");

            migrationBuilder.DropColumn(
                name: "start_minute",
                table: "staff_shifts");

            migrationBuilder.DropColumn(
                name: "bio",
                table: "business_memberships");

            migrationBuilder.DropColumn(
                name: "is_bookable",
                table: "business_memberships");

            migrationBuilder.DropColumn(
                name: "is_public",
                table: "business_memberships");
        }
    }
}
