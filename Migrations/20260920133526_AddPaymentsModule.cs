using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PunchedApi.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentsModule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "business_payment_configs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    CashEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    MpesaEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    MpesaAccountType = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    MpesaShortCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ConsumerKeyEncrypted = table.Column<string>(type: "text", nullable: true),
                    ConsumerSecretEncrypted = table.Column<string>(type: "text", nullable: true),
                    PasskeyEncrypted = table.Column<string>(type: "text", nullable: true),
                    CredentialsUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_business_payment_configs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_business_payment_configs_businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: true),
                    AppointmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Method = table.Column<int>(type: "integer", nullable: false),
                    Provider = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Reference = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ExternalReference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    PhoneNumber = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FailedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancelledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReversedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConfirmedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReversalReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_payments_appointments_AppointmentId",
                        column: x => x.AppointmentId,
                        principalTable: "appointments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_payments_businesses_BusinessId",
                        column: x => x.BusinessId,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "payment_attempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PaymentId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttemptNumber = table.Column<int>(type: "integer", nullable: false),
                    Provider = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CheckoutRequestId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    MerchantRequestId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ProviderReference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_attempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_payment_attempts_payments_PaymentId",
                        column: x => x.PaymentId,
                        principalTable: "payments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "payment_callbacks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessId = table.Column<Guid>(type: "uuid", nullable: true),
                    PaymentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    EventKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ResultCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ResultDescription = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    TransactionId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    RawPayload = table.Column<string>(type: "text", nullable: false),
                    Processed = table.Column<bool>(type: "boolean", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ProcessedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_callbacks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_payment_callbacks_payments_PaymentId",
                        column: x => x.PaymentId,
                        principalTable: "payments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_business_payment_configs_BusinessId",
                table: "business_payment_configs",
                column: "BusinessId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_business_payment_configs_MpesaShortCode",
                table: "business_payment_configs",
                column: "MpesaShortCode");

            migrationBuilder.CreateIndex(
                name: "IX_payment_attempts_CheckoutRequestId",
                table: "payment_attempts",
                column: "CheckoutRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_payment_attempts_PaymentId_AttemptNumber",
                table: "payment_attempts",
                columns: new[] { "PaymentId", "AttemptNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payment_callbacks_BusinessId_Kind_Outcome",
                table: "payment_callbacks",
                columns: new[] { "BusinessId", "Kind", "Outcome" });

            migrationBuilder.CreateIndex(
                name: "IX_payment_callbacks_EventKey",
                table: "payment_callbacks",
                column: "EventKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payment_callbacks_PaymentId",
                table: "payment_callbacks",
                column: "PaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_payments_AppointmentId",
                table: "payments",
                column: "AppointmentId");

            migrationBuilder.CreateIndex(
                name: "IX_payments_BusinessId_Status_CreatedAt",
                table: "payments",
                columns: new[] { "BusinessId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_payments_CustomerId",
                table: "payments",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_payments_ExternalReference",
                table: "payments",
                column: "ExternalReference");

            migrationBuilder.CreateIndex(
                name: "IX_payments_Reference",
                table: "payments",
                column: "Reference",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "business_payment_configs");

            migrationBuilder.DropTable(
                name: "payment_attempts");

            migrationBuilder.DropTable(
                name: "payment_callbacks");

            migrationBuilder.DropTable(
                name: "payments");
        }
    }
}
