using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PunchedApi.Migrations
{
    /// <inheritdoc />
    public partial class AddAppointmentSlotHolds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "appointment_slot_holds",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    staff_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    starts_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ends_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    service_ids_json = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    duration_minutes = table.Column<int>(type: "integer", nullable: false),
                    customer_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    customer_phone = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    customer_email = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    payment_option = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    idempotency_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    converted_appointment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_appointment_slot_holds", x => x.id);
                    table.ForeignKey(
                        name: "FK_appointment_slot_holds_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_appointment_slot_holds_users_customer_user_id",
                        column: x => x.customer_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_appointment_slot_holds_users_staff_user_id",
                        column: x => x.staff_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_appointment_slot_holds_business_staff_start",
                table: "appointment_slot_holds",
                columns: new[] { "business_id", "staff_user_id", "starts_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_appointment_slot_holds_business_status_expiry",
                table: "appointment_slot_holds",
                columns: new[] { "business_id", "status", "expires_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_appointment_slot_holds_customer_user_id",
                table: "appointment_slot_holds",
                column: "customer_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_appointment_slot_holds_staff_user_id",
                table: "appointment_slot_holds",
                column: "staff_user_id");

            migrationBuilder.CreateIndex(
                name: "ux_appointment_slot_holds_booking_key",
                table: "appointment_slot_holds",
                columns: new[] { "business_id", "idempotency_key" },
                unique: true,
                filter: "\"idempotency_key\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "appointment_slot_holds");
        }
    }
}
