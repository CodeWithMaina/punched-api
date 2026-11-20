using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PunchedApi.Migrations
{
    /// <inheritdoc />
    public partial class AddAppointmentBookingRequestKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "booking_request_key",
                table: "appointments",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_appointments_booking_request_key",
                table: "appointments",
                columns: new[] { "business_id", "booking_request_key" },
                unique: true,
                filter: "\"booking_request_key\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_appointments_booking_request_key",
                table: "appointments");

            migrationBuilder.DropColumn(
                name: "booking_request_key",
                table: "appointments");
        }
    }
}
