using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PunchedApi.Migrations
{
    /// <inheritdoc />
    public partial class AddAttendanceModule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "attendance_locations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attendance_locations", x => x.id);
                    table.ForeignKey(
                        name: "FK_attendance_locations_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_attendance_locations_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "attendance_policies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    required_verifications_json = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attendance_policies", x => x.id);
                    table.ForeignKey(
                        name: "FK_attendance_policies_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "loyalty_earning_rules",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    program_id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<int>(type: "integer", nullable: false),
                    stamp_amount = table.Column<int>(type: "integer", nullable: false),
                    stamping_mode = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    qualifying_service_id = table.Column<Guid>(type: "uuid", nullable: true),
                    activated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_loyalty_earning_rules", x => x.id);
                    table.CheckConstraint("chk_loyalty_earning_rule_stamp_amount_positive", "\"stamp_amount\" >= 1");
                    table.ForeignKey(
                        name: "FK_loyalty_earning_rules_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_loyalty_earning_rules_loyalty_programs_program_id",
                        column: x => x.program_id,
                        principalTable: "loyalty_programs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "rewards",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    program_id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    required_stamps = table.Column<int>(type: "integer", nullable: false),
                    type = table.Column<int>(type: "integer", nullable: false),
                    value = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    percentage = table.Column<int>(type: "integer", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    expiration_hours = table.Column<int>(type: "integer", nullable: false),
                    stamps_to_consume = table.Column<int>(type: "integer", nullable: false),
                    service_catalog_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rewards", x => x.id);
                    table.CheckConstraint("chk_rewards_required_stamps_positive", "\"required_stamps\" >= 1");
                    table.CheckConstraint("chk_rewards_stamps_to_consume_non_negative", "\"stamps_to_consume\" >= 0");
                    table.ForeignKey(
                        name: "FK_rewards_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_rewards_loyalty_programs_program_id",
                        column: x => x.program_id,
                        principalTable: "loyalty_programs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "attendance_qr_credentials",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attendance_location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revoked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    revoked_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    last_used_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attendance_qr_credentials", x => x.id);
                    table.ForeignKey(
                        name: "FK_attendance_qr_credentials_attendance_locations_attendance_l~",
                        column: x => x.attendance_location_id,
                        principalTable: "attendance_locations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_attendance_qr_credentials_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_attendance_qr_credentials_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stamp_transactions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    program_id = table.Column<Guid>(type: "uuid", nullable: false),
                    card_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<int>(type: "integer", nullable: false),
                    direction = table.Column<int>(type: "integer", nullable: false),
                    source = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    source_id = table.Column<Guid>(type: "uuid", nullable: true),
                    earning_rule_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_automatic = table.Column<bool>(type: "boolean", nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_by_role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    idempotency_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    metadata_json = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stamp_transactions", x => x.id);
                    table.CheckConstraint("chk_stamp_transaction_amount_positive", "\"amount\" >= 1");
                    table.ForeignKey(
                        name: "FK_stamp_transactions_loyalty_cards_card_id",
                        column: x => x.card_id,
                        principalTable: "loyalty_cards",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_stamp_transactions_loyalty_earning_rules_earning_rule_id",
                        column: x => x.earning_rule_id,
                        principalTable: "loyalty_earning_rules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_stamp_transactions_loyalty_programs_program_id",
                        column: x => x.program_id,
                        principalTable: "loyalty_programs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "reward_entitlements",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    program_id = table.Column<Guid>(type: "uuid", nullable: false),
                    card_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reward_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reward_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    required_stamps = table.Column<int>(type: "integer", nullable: false),
                    stamps_to_consume = table.Column<int>(type: "integer", nullable: false),
                    reward_value = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    unlocked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    redeemed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    redemption_id = table.Column<Guid>(type: "uuid", nullable: true),
                    unlock_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reward_entitlements", x => x.id);
                    table.ForeignKey(
                        name: "FK_reward_entitlements_loyalty_cards_card_id",
                        column: x => x.card_id,
                        principalTable: "loyalty_cards",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_reward_entitlements_loyalty_programs_program_id",
                        column: x => x.program_id,
                        principalTable: "loyalty_programs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_reward_entitlements_redemptions_redemption_id",
                        column: x => x.redemption_id,
                        principalTable: "redemptions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_reward_entitlements_rewards_reward_id",
                        column: x => x.reward_id,
                        principalTable: "rewards",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "attendance_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    staff_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    attendance_location_id = table.Column<Guid>(type: "uuid", nullable: true),
                    attendance_qr_credential_id = table.Column<Guid>(type: "uuid", nullable: true),
                    attendance_session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    verification_summary_json = table.Column<string>(type: "text", nullable: false),
                    client_idempotency_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recorded_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attendance_events", x => x.id);
                    table.CheckConstraint("chk_attendance_events_clockout_requires_session", "\"event_type\" <> 'ClockOut' OR \"attendance_session_id\" IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_attendance_events_attendance_locations_attendance_location_~",
                        column: x => x.attendance_location_id,
                        principalTable: "attendance_locations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_attendance_events_attendance_qr_credentials_attendance_qr_c~",
                        column: x => x.attendance_qr_credential_id,
                        principalTable: "attendance_qr_credentials",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_attendance_events_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_attendance_events_users_staff_user_id",
                        column: x => x.staff_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "attendance_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    staff_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    opening_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    closing_event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    opened_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    closed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    opening_location_id = table.Column<Guid>(type: "uuid", nullable: true),
                    closing_location_id = table.Column<Guid>(type: "uuid", nullable: true),
                    worked_minutes = table.Column<int>(type: "integer", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attendance_sessions", x => x.id);
                    table.CheckConstraint("chk_attendance_sessions_close_consistency", "\"closed_at\" IS NULL OR (\"closing_event_id\" IS NOT NULL AND \"closed_at\" >= \"opened_at\")");
                    table.ForeignKey(
                        name: "FK_attendance_sessions_attendance_events_closing_event_id",
                        column: x => x.closing_event_id,
                        principalTable: "attendance_events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_attendance_sessions_attendance_events_opening_event_id",
                        column: x => x.opening_event_id,
                        principalTable: "attendance_events",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_attendance_sessions_attendance_locations_closing_location_id",
                        column: x => x.closing_location_id,
                        principalTable: "attendance_locations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_attendance_sessions_attendance_locations_opening_location_id",
                        column: x => x.opening_location_id,
                        principalTable: "attendance_locations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_attendance_sessions_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_attendance_sessions_users_staff_user_id",
                        column: x => x.staff_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_attendance_events_business_occurred",
                table: "attendance_events",
                columns: new[] { "business_id", "occurred_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_attendance_events_idempotency",
                table: "attendance_events",
                columns: new[] { "business_id", "staff_user_id", "client_idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_attendance_events_location_occurred",
                table: "attendance_events",
                columns: new[] { "attendance_location_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_attendance_events_qr_credential",
                table: "attendance_events",
                column: "attendance_qr_credential_id");

            migrationBuilder.CreateIndex(
                name: "ix_attendance_events_session",
                table: "attendance_events",
                column: "attendance_session_id");

            migrationBuilder.CreateIndex(
                name: "ix_attendance_events_staff_occurred",
                table: "attendance_events",
                columns: new[] { "staff_user_id", "occurred_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_attendance_locations_business_active",
                table: "attendance_locations",
                columns: new[] { "business_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "IX_attendance_locations_created_by_user_id",
                table: "attendance_locations",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_attendance_policies_business",
                table: "attendance_policies",
                column: "business_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_attendance_qr_credentials_business",
                table: "attendance_qr_credentials",
                columns: new[] { "business_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_attendance_qr_credentials_created_by_user_id",
                table: "attendance_qr_credentials",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_attendance_qr_credentials_one_active_per_location",
                table: "attendance_qr_credentials",
                column: "attendance_location_id",
                unique: true,
                filter: "\"status\" = 'Active'");

            migrationBuilder.CreateIndex(
                name: "ix_attendance_qr_credentials_token_hash",
                table: "attendance_qr_credentials",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_attendance_sessions_business_opened",
                table: "attendance_sessions",
                columns: new[] { "business_id", "opened_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_attendance_sessions_closing_event_id",
                table: "attendance_sessions",
                column: "closing_event_id");

            migrationBuilder.CreateIndex(
                name: "IX_attendance_sessions_closing_location_id",
                table: "attendance_sessions",
                column: "closing_location_id");

            migrationBuilder.CreateIndex(
                name: "ix_attendance_sessions_open_per_staff",
                table: "attendance_sessions",
                column: "staff_user_id",
                unique: true,
                filter: "\"closed_at\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_attendance_sessions_opening_event_id",
                table: "attendance_sessions",
                column: "opening_event_id");

            migrationBuilder.CreateIndex(
                name: "IX_attendance_sessions_opening_location_id",
                table: "attendance_sessions",
                column: "opening_location_id");

            migrationBuilder.CreateIndex(
                name: "ix_attendance_sessions_staff_opened",
                table: "attendance_sessions",
                columns: new[] { "staff_user_id", "opened_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_earning_rules_BusinessId_Status",
                table: "loyalty_earning_rules",
                columns: new[] { "business_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_earning_rules_ProgramId_Source",
                table: "loyalty_earning_rules",
                columns: new[] { "program_id", "source" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_reward_entitlements_BusinessId_Status",
                table: "reward_entitlements",
                columns: new[] { "business_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_reward_entitlements_CardId_Status",
                table: "reward_entitlements",
                columns: new[] { "card_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_reward_entitlements_program_id",
                table: "reward_entitlements",
                column: "program_id");

            migrationBuilder.CreateIndex(
                name: "IX_reward_entitlements_redemption_id",
                table: "reward_entitlements",
                column: "redemption_id");

            migrationBuilder.CreateIndex(
                name: "IX_reward_entitlements_reward_id",
                table: "reward_entitlements",
                column: "reward_id");

            migrationBuilder.CreateIndex(
                name: "IX_reward_entitlements_UnlockKey",
                table: "reward_entitlements",
                column: "unlock_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_rewards_BusinessId",
                table: "rewards",
                column: "business_id");

            migrationBuilder.CreateIndex(
                name: "IX_rewards_ProgramId_Status",
                table: "rewards",
                columns: new[] { "program_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_stamp_transactions_BusinessId_CreatedAt",
                table: "stamp_transactions",
                columns: new[] { "business_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_stamp_transactions_CardId_CreatedAt",
                table: "stamp_transactions",
                columns: new[] { "card_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_stamp_transactions_CustomerId_CreatedAt",
                table: "stamp_transactions",
                columns: new[] { "customer_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_stamp_transactions_earning_rule_id",
                table: "stamp_transactions",
                column: "earning_rule_id");

            migrationBuilder.CreateIndex(
                name: "IX_stamp_transactions_IdempotencyKey",
                table: "stamp_transactions",
                column: "idempotency_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_stamp_transactions_program_id",
                table: "stamp_transactions",
                column: "program_id");

            migrationBuilder.AddForeignKey(
                name: "FK_attendance_events_attendance_sessions_attendance_session_id",
                table: "attendance_events",
                column: "attendance_session_id",
                principalTable: "attendance_sessions",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_attendance_events_attendance_locations_attendance_location_~",
                table: "attendance_events");

            migrationBuilder.DropForeignKey(
                name: "FK_attendance_qr_credentials_attendance_locations_attendance_l~",
                table: "attendance_qr_credentials");

            migrationBuilder.DropForeignKey(
                name: "FK_attendance_sessions_attendance_locations_closing_location_id",
                table: "attendance_sessions");

            migrationBuilder.DropForeignKey(
                name: "FK_attendance_sessions_attendance_locations_opening_location_id",
                table: "attendance_sessions");

            migrationBuilder.DropForeignKey(
                name: "FK_attendance_events_attendance_qr_credentials_attendance_qr_c~",
                table: "attendance_events");

            migrationBuilder.DropForeignKey(
                name: "FK_attendance_events_attendance_sessions_attendance_session_id",
                table: "attendance_events");

            migrationBuilder.DropTable(
                name: "attendance_policies");

            migrationBuilder.DropTable(
                name: "reward_entitlements");

            migrationBuilder.DropTable(
                name: "stamp_transactions");

            migrationBuilder.DropTable(
                name: "rewards");

            migrationBuilder.DropTable(
                name: "loyalty_earning_rules");

            migrationBuilder.DropTable(
                name: "attendance_locations");

            migrationBuilder.DropTable(
                name: "attendance_qr_credentials");

            migrationBuilder.DropTable(
                name: "attendance_sessions");

            migrationBuilder.DropTable(
                name: "attendance_events");
        }
    }
}
