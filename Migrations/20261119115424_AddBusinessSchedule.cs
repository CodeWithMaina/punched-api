using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PunchedApi.Migrations
{
    /// <inheritdoc />
    public partial class AddBusinessSchedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "business_operating_hours",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day_of_week = table.Column<int>(type: "integer", nullable: false),
                    is_closed = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_business_operating_hours", x => x.id);
                    table.CheckConstraint("chk_business_operating_hours_day", "\"day_of_week\" >= 0 AND \"day_of_week\" <= 6");
                    table.ForeignKey(
                        name: "FK_business_operating_hours_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "business_schedule_exceptions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    is_closed = table.Column<bool>(type: "boolean", nullable: false),
                    reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_business_schedule_exceptions", x => x.id);
                    table.ForeignKey(
                        name: "FK_business_schedule_exceptions_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "business_operating_periods",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_operating_hour_id = table.Column<Guid>(type: "uuid", nullable: false),
                    start_minute = table.Column<int>(type: "integer", nullable: false),
                    end_minute = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_business_operating_periods", x => x.id);
                    table.CheckConstraint("chk_business_operating_periods_minutes", "\"start_minute\" >= 0 AND \"start_minute\" <= 1439 AND \"end_minute\" >= 1 AND \"end_minute\" <= 1440 AND \"end_minute\" > \"start_minute\"");
                    table.ForeignKey(
                        name: "FK_business_operating_periods_business_operating_hours_busines~",
                        column: x => x.business_operating_hour_id,
                        principalTable: "business_operating_hours",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "business_schedule_exception_periods",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_schedule_exception_id = table.Column<Guid>(type: "uuid", nullable: false),
                    start_minute = table.Column<int>(type: "integer", nullable: false),
                    end_minute = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_business_schedule_exception_periods", x => x.id);
                    table.CheckConstraint("chk_business_schedule_exception_periods_minutes", "\"start_minute\" >= 0 AND \"start_minute\" <= 1439 AND \"end_minute\" >= 1 AND \"end_minute\" <= 1440 AND \"end_minute\" > \"start_minute\"");
                    table.ForeignKey(
                        name: "FK_business_schedule_exception_periods_business_schedule_excep~",
                        column: x => x.business_schedule_exception_id,
                        principalTable: "business_schedule_exceptions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_business_operating_hours_business_day",
                table: "business_operating_hours",
                columns: new[] { "business_id", "day_of_week" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_business_operating_periods_hour",
                table: "business_operating_periods",
                column: "business_operating_hour_id");

            migrationBuilder.CreateIndex(
                name: "ix_business_schedule_exception_periods_exception",
                table: "business_schedule_exception_periods",
                column: "business_schedule_exception_id");

            migrationBuilder.CreateIndex(
                name: "ux_business_schedule_exceptions_business_date",
                table: "business_schedule_exceptions",
                columns: new[] { "business_id", "date" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "business_operating_periods");

            migrationBuilder.DropTable(
                name: "business_schedule_exception_periods");

            migrationBuilder.DropTable(
                name: "business_operating_hours");

            migrationBuilder.DropTable(
                name: "business_schedule_exceptions");
        }
    }
}
