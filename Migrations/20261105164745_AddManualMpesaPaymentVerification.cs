using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PunchedApi.Migrations
{
    /// <inheritdoc />
    public partial class AddManualMpesaPaymentVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "ParsedAmount",
                table: "payments",
                type: "numeric(12,2)",
                precision: 12,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ParsedRecipient",
                table: "payments",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ParsedTransactionAt",
                table: "payments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ParsedTransactionCode",
                table: "payments",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RejectedAt",
                table: "payments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RejectedByUserId",
                table: "payments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RejectionReason",
                table: "payments",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SubmittedAt",
                table: "payments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SubmittedMessage",
                table: "payments",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "VerifiedAt",
                table: "payments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "VerifiedByUserId",
                table: "payments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "BookingFeePercentage",
                table: "business_payment_configs",
                type: "numeric",
                nullable: false,
                // 50 preserves the half-up-front behaviour every existing business
                // already had, so enabling the feature does not silently change what
                // anyone is charged. A business that wants no fee sets it to 0.
                defaultValue: 50m);

            migrationBuilder.AddColumn<bool>(
                name: "ManualMpesaEnabled",
                table: "business_payment_configs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "MpesaInstructions",
                table: "business_payment_configs",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MpesaPaybillAccount",
                table: "business_payment_configs",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MpesaPaybillNumber",
                table: "business_payment_configs",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MpesaPayeeName",
                table: "business_payment_configs",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MpesaTillNumber",
                table: "business_payment_configs",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "payment_verification_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    PaymentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActorName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    TransactionCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Message = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_verification_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_payment_verification_events_payments_PaymentId",
                        column: x => x.PaymentId,
                        principalTable: "payments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_payments_ParsedTransactionCode",
                table: "payments",
                column: "ParsedTransactionCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payment_verification_events_BusinessId_CreatedAt",
                table: "payment_verification_events",
                columns: new[] { "BusinessId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_payment_verification_events_PaymentId",
                table: "payment_verification_events",
                column: "PaymentId");

            // A booking that is waiting on its booking fee still occupies its staff
            // member's interval: the customer has committed to that time, and releasing
            // it would let a second customer book a slot the first is paying for.
            //
            // This only ever WIDENS the constraint (the previous predicate rejected
            // every overlap among non-cancelled rows, which is a superset of the
            // overlaps rejected now), so no existing row can violate it. The predicate
            // text matches AppointmentReservations.ReservationPredicateSql.
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS btree_gist;");

            migrationBuilder.Sql(
                "ALTER TABLE appointments DROP CONSTRAINT IF EXISTS appointments_no_staff_overlap;");

            migrationBuilder.Sql(
                """
                ALTER TABLE appointments
                ADD CONSTRAINT appointments_no_staff_overlap
                EXCLUDE USING gist (
                    staff_user_id WITH =,
                    tstzrange(scheduled_at, end_at) WITH &&
                )
                WHERE (status IN ('pending_payment', 'booked', 'confirmed'));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "payment_verification_events");

            migrationBuilder.DropIndex(
                name: "IX_payments_ParsedTransactionCode",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "ParsedAmount",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "ParsedRecipient",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "ParsedTransactionAt",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "ParsedTransactionCode",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "RejectedAt",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "RejectedByUserId",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "RejectionReason",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "SubmittedAt",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "SubmittedMessage",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "VerifiedAt",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "VerifiedByUserId",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "BookingFeePercentage",
                table: "business_payment_configs");

            migrationBuilder.DropColumn(
                name: "ManualMpesaEnabled",
                table: "business_payment_configs");

            migrationBuilder.DropColumn(
                name: "MpesaInstructions",
                table: "business_payment_configs");

            migrationBuilder.DropColumn(
                name: "MpesaPaybillAccount",
                table: "business_payment_configs");

            migrationBuilder.DropColumn(
                name: "MpesaPaybillNumber",
                table: "business_payment_configs");

            migrationBuilder.DropColumn(
                name: "MpesaPayeeName",
                table: "business_payment_configs");

            migrationBuilder.DropColumn(
                name: "MpesaTillNumber",
                table: "business_payment_configs");

            // Restore the previous reservation rule. It is narrower, so re-adding it
            // cannot fail on existing rows; any pending-payment booking that was
            // reserving time simply stops doing so, which is the pre-migration state.
            migrationBuilder.Sql(
                "ALTER TABLE appointments DROP CONSTRAINT IF EXISTS appointments_no_staff_overlap;");

            migrationBuilder.Sql(
                """
                ALTER TABLE appointments
                ADD CONSTRAINT appointments_no_staff_overlap
                EXCLUDE USING gist (
                    staff_user_id WITH =,
                    tstzrange(scheduled_at, end_at) WITH &&
                )
                WHERE (status IN ('booked', 'confirmed'));
                """);
        }
    }
}
