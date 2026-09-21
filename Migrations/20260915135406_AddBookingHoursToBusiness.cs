using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PunchedApi.Migrations
{
    /// <inheritdoc />
    public partial class AddBookingHoursToBusiness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "booking_close_hour",
                table: "businesses",
                type: "integer",
                nullable: false,
                defaultValue: 18);

            migrationBuilder.AddColumn<int>(
                name: "booking_lead_time_minutes",
                table: "businesses",
                type: "integer",
                nullable: false,
                defaultValue: 60);

            migrationBuilder.AddColumn<int>(
                name: "booking_open_hour",
                table: "businesses",
                type: "integer",
                nullable: false,
                defaultValue: 9);

            migrationBuilder.AddColumn<int>(
                name: "booking_slot_interval_minutes",
                table: "businesses",
                type: "integer",
                nullable: false,
                defaultValue: 15);

            migrationBuilder.AddColumn<string>(
                name: "time_zone_id",
                table: "businesses",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "Africa/Nairobi");

            migrationBuilder.AddCheckConstraint(
                name: "chk_business_booking_hours",
                table: "businesses",
                sql: "\"booking_open_hour\" >= 0 AND \"booking_open_hour\" <= 23 AND \"booking_close_hour\" >= 1 AND \"booking_close_hour\" <= 24 AND \"booking_close_hour\" > \"booking_open_hour\"");

            migrationBuilder.AddCheckConstraint(
                name: "chk_business_booking_interval",
                table: "businesses",
                sql: "\"booking_slot_interval_minutes\" >= 5 AND \"booking_slot_interval_minutes\" <= 240");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "chk_business_booking_hours",
                table: "businesses");

            migrationBuilder.DropCheckConstraint(
                name: "chk_business_booking_interval",
                table: "businesses");

            migrationBuilder.DropColumn(
                name: "booking_close_hour",
                table: "businesses");

            migrationBuilder.DropColumn(
                name: "booking_lead_time_minutes",
                table: "businesses");

            migrationBuilder.DropColumn(
                name: "booking_open_hour",
                table: "businesses");

            migrationBuilder.DropColumn(
                name: "booking_slot_interval_minutes",
                table: "businesses");

            migrationBuilder.DropColumn(
                name: "time_zone_id",
                table: "businesses");
        }
    }
}
