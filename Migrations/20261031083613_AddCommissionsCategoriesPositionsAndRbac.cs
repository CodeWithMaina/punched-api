using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PunchedApi.Migrations
{
    /// <inheritdoc />
    public partial class AddCommissionsCategoriesPositionsAndRbac : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_RescheduleRequests",
                table: "RescheduleRequests");

            migrationBuilder.RenameTable(
                name: "RescheduleRequests",
                newName: "reschedule_requests");

            migrationBuilder.RenameColumn(
                name: "Showcase",
                table: "services",
                newName: "showcase");

            migrationBuilder.RenameColumn(
                name: "Description",
                table: "services",
                newName: "description");

            migrationBuilder.RenameColumn(
                name: "Status",
                table: "reschedule_requests",
                newName: "status");

            migrationBuilder.RenameColumn(
                name: "Id",
                table: "reschedule_requests",
                newName: "id");

            migrationBuilder.RenameColumn(
                name: "ResolvedByUserId",
                table: "reschedule_requests",
                newName: "resolved_by_user_id");

            migrationBuilder.RenameColumn(
                name: "ResolvedAt",
                table: "reschedule_requests",
                newName: "resolved_at");

            migrationBuilder.RenameColumn(
                name: "RequestedByUserId",
                table: "reschedule_requests",
                newName: "requested_by_user_id");

            migrationBuilder.RenameColumn(
                name: "ProposedStaffUserId",
                table: "reschedule_requests",
                newName: "proposed_staff_user_id");

            migrationBuilder.RenameColumn(
                name: "ProposedServicesJson",
                table: "reschedule_requests",
                newName: "proposed_services_json");

            migrationBuilder.RenameColumn(
                name: "ProposedScheduledAt",
                table: "reschedule_requests",
                newName: "proposed_scheduled_at");

            migrationBuilder.RenameColumn(
                name: "ProposedEndAt",
                table: "reschedule_requests",
                newName: "proposed_end_at");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                table: "reschedule_requests",
                newName: "created_at");

            migrationBuilder.RenameColumn(
                name: "AppointmentId",
                table: "reschedule_requests",
                newName: "appointment_id");

            migrationBuilder.AlterColumn<bool>(
                name: "showcase",
                table: "services",
                type: "boolean",
                nullable: false,
                defaultValue: true,
                oldClrType: typeof(bool),
                oldType: "boolean");

            migrationBuilder.AddColumn<Guid>(
                name: "category_id",
                table: "services",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "position_id",
                table: "business_memberships",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "version",
                table: "appointments",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "business_id",
                table: "reschedule_requests",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "decision_notes",
                table: "reschedule_requests",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "expires_at",
                table: "reschedule_requests",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "original_end_at",
                table: "reschedule_requests",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "original_scheduled_at",
                table: "reschedule_requests",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<Guid>(
                name: "original_staff_user_id",
                table: "reschedule_requests",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "request_reason",
                table: "reschedule_requests",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "requested_by_party",
                table: "reschedule_requests",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "updated_at",
                table: "reschedule_requests",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<int>(
                name: "version",
                table: "reschedule_requests",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddPrimaryKey(
                name: "PK_reschedule_requests",
                table: "reschedule_requests",
                column: "id");

            migrationBuilder.CreateTable(
                name: "business_roles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    is_owner_role = table.Column<bool>(type: "boolean", nullable: false),
                    is_system = table.Column<bool>(type: "boolean", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_business_roles", x => x.id);
                    table.ForeignKey(
                        name: "FK_business_roles_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "commission_configs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    calculation_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    default_percentage = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: true),
                    default_fixed_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: true),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    require_settlement = table.Column<bool>(type: "boolean", nullable: false),
                    require_approval = table.Column<bool>(type: "boolean", nullable: false),
                    earn_on_no_show = table.Column<bool>(type: "boolean", nullable: false),
                    earn_on_cancelled = table.Column<bool>(type: "boolean", nullable: false),
                    allow_partial_settlement = table.Column<bool>(type: "boolean", nullable: false),
                    minimum_settlement_ratio = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: false),
                    require_staff_service_eligibility = table.Column<bool>(type: "boolean", nullable: false),
                    rounding_decimals = table.Column<int>(type: "integer", nullable: false),
                    notes = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_commission_configs", x => x.id);
                    table.CheckConstraint("ck_commission_configs_calculation_type", "calculation_type IN ('Percentage', 'Fixed')");
                    table.CheckConstraint("ck_commission_configs_fixed_non_negative", "default_fixed_amount IS NULL OR default_fixed_amount >= 0");
                    table.CheckConstraint("ck_commission_configs_percentage_range", "default_percentage IS NULL OR (default_percentage >= 0 AND default_percentage <= 100)");
                    table.CheckConstraint("ck_commission_configs_rounding", "rounding_decimals >= 0 AND rounding_decimals <= 4");
                    table.CheckConstraint("ck_commission_configs_settlement_ratio", "minimum_settlement_ratio >= 0 AND minimum_settlement_ratio <= 1");
                    table.ForeignKey(
                        name: "FK_commission_configs_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "commission_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    staff_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    appointment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    appointment_resource_id = table.Column<Guid>(type: "uuid", nullable: true),
                    service_id = table.Column<Guid>(type: "uuid", nullable: true),
                    payment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    payout_id = table.Column<Guid>(type: "uuid", nullable: true),
                    entry_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    source_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    config_scope = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    config_id = table.Column<Guid>(type: "uuid", nullable: true),
                    config_snapshot_json = table.Column<string>(type: "text", nullable: false),
                    calculation_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    rate_percent = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: true),
                    fixed_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: true),
                    base_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    adjustment_of_entry_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reverses_entry_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reversed_by_entry_id = table.Column<Guid>(type: "uuid", nullable: true),
                    dedupe_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    approved_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    payable_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    paid_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    disputed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    disputed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    dispute_reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    reversed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    voided_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    void_reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_commission_entries", x => x.id);
                    table.CheckConstraint("ck_commission_entries_calculation_type", "calculation_type IN ('Percentage', 'Fixed')");
                    table.CheckConstraint("ck_commission_entries_config_scope", "config_scope IN ('Business', 'Staff', 'Service', 'StaffService')");
                    table.CheckConstraint("ck_commission_entries_entry_type", "entry_type IN ('Earning', 'Adjustment', 'Reversal')");
                    table.CheckConstraint("ck_commission_entries_reason_required", "entry_type = 'Earning' OR (reason IS NOT NULL AND length(trim(reason)) > 0)");
                    table.CheckConstraint("ck_commission_entries_source_type", "source_type IN ('Appointment', 'Manual')");
                    table.CheckConstraint("ck_commission_entries_status", "status IN ('Pending', 'Approved', 'Payable', 'Paid', 'Disputed', 'Reversed', 'Void')");
                    table.ForeignKey(
                        name: "FK_commission_entries_appointments_appointment_id",
                        column: x => x.appointment_id,
                        principalTable: "appointments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_commission_entries_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_commission_entries_payments_payment_id",
                        column: x => x.payment_id,
                        principalTable: "payments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_commission_entries_services_service_id",
                        column: x => x.service_id,
                        principalTable: "services",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_commission_entries_users_customer_id",
                        column: x => x.customer_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_commission_entries_users_staff_user_id",
                        column: x => x.staff_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "commission_payouts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    staff_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    total_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    entry_count = table.Column<int>(type: "integer", nullable: false),
                    period_start_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    period_end_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    paid_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_commission_payouts", x => x.id);
                    table.CheckConstraint("ck_commission_payouts_entry_count", "entry_count >= 0");
                    table.CheckConstraint("ck_commission_payouts_total_non_negative", "total_amount >= 0");
                    table.ForeignKey(
                        name: "FK_commission_payouts_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_commission_payouts_users_staff_user_id",
                        column: x => x.staff_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "commission_rates",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    staff_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    service_id = table.Column<Guid>(type: "uuid", nullable: true),
                    scope_key = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    calculation_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    percentage = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: true),
                    fixed_amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_commission_rates", x => x.id);
                    table.CheckConstraint("ck_commission_rates_calculation_type", "calculation_type IN ('Percentage', 'Fixed')");
                    table.CheckConstraint("ck_commission_rates_fixed_non_negative", "fixed_amount IS NULL OR fixed_amount >= 0");
                    table.CheckConstraint("ck_commission_rates_percentage_range", "percentage IS NULL OR (percentage >= 0 AND percentage <= 100)");
                    table.CheckConstraint("ck_commission_rates_scope", "scope IN ('Business', 'Staff', 'Service', 'StaffService')");
                    table.CheckConstraint("ck_commission_rates_scope_target", "(scope = 'Business' AND staff_user_id IS NULL AND service_id IS NULL) OR (scope = 'Staff' AND staff_user_id IS NOT NULL AND service_id IS NULL) OR (scope = 'Service' AND staff_user_id IS NULL AND service_id IS NOT NULL) OR (scope = 'StaffService' AND staff_user_id IS NOT NULL AND service_id IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_commission_rates_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_commission_rates_services_service_id",
                        column: x => x.service_id,
                        principalTable: "services",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_commission_rates_users_staff_user_id",
                        column: x => x.staff_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "permissions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    module_key = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    is_system = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_permissions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "service_categories",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_categories", x => x.id);
                    table.ForeignKey(
                        name: "FK_service_categories_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "staff_positions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_staff_positions", x => x.id);
                    table.ForeignKey(
                        name: "FK_staff_positions_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "business_membership_role_assignments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assigned_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_business_membership_role_assignments", x => x.id);
                    table.ForeignKey(
                        name: "FK_business_membership_role_assignments_business_memberships_b~",
                        column: x => x.business_membership_id,
                        principalTable: "business_memberships",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_business_membership_role_assignments_business_roles_busines~",
                        column: x => x.business_role_id,
                        principalTable: "business_roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_business_membership_role_assignments_users_assigned_by_user~",
                        column: x => x.assigned_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "business_role_permissions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    permission_code = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_business_role_permissions", x => x.id);
                    table.ForeignKey(
                        name: "FK_business_role_permissions_business_roles_business_role_id",
                        column: x => x.business_role_id,
                        principalTable: "business_roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "staff_position_history",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    staff_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position_id = table.Column<Guid>(type: "uuid", nullable: true),
                    position_name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    changed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    changed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_staff_position_history", x => x.id);
                    table.ForeignKey(
                        name: "FK_staff_position_history_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_staff_position_history_staff_positions_position_id",
                        column: x => x.position_id,
                        principalTable: "staff_positions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_staff_position_history_users_changed_by_user_id",
                        column: x => x.changed_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_staff_position_history_users_staff_user_id",
                        column: x => x.staff_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_services_business_category",
                table: "services",
                columns: new[] { "business_id", "category_id" });

            migrationBuilder.CreateIndex(
                name: "IX_services_category_id",
                table: "services",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "ix_business_memberships_business_position",
                table: "business_memberships",
                columns: new[] { "business_id", "position_id" });

            migrationBuilder.CreateIndex(
                name: "IX_business_memberships_position_id",
                table: "business_memberships",
                column: "position_id");

            migrationBuilder.CreateIndex(
                name: "ix_reschedule_requests_appointment_status",
                table: "reschedule_requests",
                columns: new[] { "appointment_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_reschedule_requests_business_status_created",
                table: "reschedule_requests",
                columns: new[] { "business_id", "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_reschedule_requests_original_staff_user_id",
                table: "reschedule_requests",
                column: "original_staff_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_reschedule_requests_proposed_staff_user_id",
                table: "reschedule_requests",
                column: "proposed_staff_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_reschedule_requests_requested_by_user_id",
                table: "reschedule_requests",
                column: "requested_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_reschedule_requests_resolved_by_user_id",
                table: "reschedule_requests",
                column: "resolved_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_reschedule_requests_status_expires",
                table: "reschedule_requests",
                columns: new[] { "status", "expires_at" });

            // ── Data migration: existing reschedule requests ──────────────────
            // The new columns on reschedule_requests are NOT NULL with sentinel
            // defaults (all-zero uuid, year 1, empty string) because a generated
            // migration cannot know how to populate them. The CHECK constraints
            // immediately below and the foreign keys further down would therefore
            // reject any database that already holds reschedule rows, so the
            // historical records are backfilled from their linked appointment
            // FIRST — this statement must stay ahead of both.
            //
            // Every value is derived from data that already existed:
            //   business_id            <- the appointment's business (the tenant)
            //   original_scheduled_at  <- the appointment's schedule, which IS the
            //                             pre-change schedule of a resolved request
            //                             and the only known one for a pending one
            //   original_end_at        <- likewise
            //   original_staff_user_id <- the appointment's assigned staff member
            //   updated_at             <- created_at (nothing more precise exists)
            //   requested_by_party     <- 'Customer': the enum is persisted by name
            //                             (HasConversion<string>) and the historical
            //                             flow was customer-initiated with owner
            //                             approval, which is what the legacy
            //                             producer notified. Must match
            //                             ck_reschedule_requests_party exactly.
            //   status                 <- already lowercase wire values, unchanged
            //
            // Rows whose appointment is missing cannot satisfy the appointment
            // foreign key either, so they are removed rather than left to abort the
            // migration. On a clean database both statements affect zero rows.
            migrationBuilder.Sql("""
                UPDATE reschedule_requests AS r
                SET business_id            = a.business_id,
                    original_scheduled_at  = CASE WHEN r.original_scheduled_at <= TIMESTAMPTZ '0001-01-02 00:00:00+00'
                                                  THEN a.scheduled_at ELSE r.original_scheduled_at END,
                    original_end_at        = CASE WHEN r.original_end_at <= TIMESTAMPTZ '0001-01-02 00:00:00+00'
                                                  THEN a.end_at ELSE r.original_end_at END,
                    original_staff_user_id = COALESCE(r.original_staff_user_id, a.staff_user_id),
                    updated_at             = CASE WHEN r.updated_at <= TIMESTAMPTZ '0001-01-02 00:00:00+00'
                                                  THEN r.created_at ELSE r.updated_at END,
                    requested_by_party     = CASE WHEN r.requested_by_party = '' THEN 'Customer'
                                                  ELSE r.requested_by_party END
                FROM appointments AS a
                WHERE a.id = r.appointment_id
                  AND (r.business_id = '00000000-0000-0000-0000-000000000000'
                       OR r.updated_at <= TIMESTAMPTZ '0001-01-02 00:00:00+00'
                       OR r.requested_by_party = '');
                """);

            migrationBuilder.Sql("""
                DELETE FROM reschedule_requests AS r
                WHERE NOT EXISTS (SELECT 1 FROM appointments AS a WHERE a.id = r.appointment_id);
                """);


            migrationBuilder.AddCheckConstraint(
                name: "ck_reschedule_requests_party",
                table: "reschedule_requests",
                sql: "requested_by_party IN ('Customer', 'Staff', 'Owner')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_reschedule_requests_status",
                table: "reschedule_requests",
                sql: "status IN ('pending', 'approved', 'rejected', 'withdrawn', 'expired')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_reschedule_requests_window",
                table: "reschedule_requests",
                sql: "proposed_end_at > proposed_scheduled_at");

            migrationBuilder.CreateIndex(
                name: "IX_business_membership_role_assignments_assigned_by_user_id",
                table: "business_membership_role_assignments",
                column: "assigned_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_membership_role_assignments_role",
                table: "business_membership_role_assignments",
                column: "business_role_id");

            migrationBuilder.CreateIndex(
                name: "ux_membership_role_assignments_membership_role",
                table: "business_membership_role_assignments",
                columns: new[] { "business_membership_id", "business_role_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_business_role_permissions_code",
                table: "business_role_permissions",
                column: "permission_code");

            migrationBuilder.CreateIndex(
                name: "ux_business_role_permissions_role_code",
                table: "business_role_permissions",
                columns: new[] { "business_role_id", "permission_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_business_roles_business_key",
                table: "business_roles",
                columns: new[] { "business_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_business_roles_business_name",
                table: "business_roles",
                columns: new[] { "business_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_business_roles_single_owner",
                table: "business_roles",
                column: "business_id",
                unique: true,
                filter: "is_owner_role = true");

            migrationBuilder.CreateIndex(
                name: "ux_commission_configs_business",
                table: "commission_configs",
                column: "business_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_commission_entries_appointment",
                table: "commission_entries",
                column: "appointment_id");

            migrationBuilder.CreateIndex(
                name: "ix_commission_entries_business_staff_status",
                table: "commission_entries",
                columns: new[] { "business_id", "staff_user_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_commission_entries_business_status_created",
                table: "commission_entries",
                columns: new[] { "business_id", "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_commission_entries_customer_id",
                table: "commission_entries",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "IX_commission_entries_payment_id",
                table: "commission_entries",
                column: "payment_id");

            migrationBuilder.CreateIndex(
                name: "ix_commission_entries_payout",
                table: "commission_entries",
                column: "payout_id");

            migrationBuilder.CreateIndex(
                name: "IX_commission_entries_service_id",
                table: "commission_entries",
                column: "service_id");

            migrationBuilder.CreateIndex(
                name: "IX_commission_entries_staff_user_id",
                table: "commission_entries",
                column: "staff_user_id");

            migrationBuilder.CreateIndex(
                name: "ux_commission_entries_dedupe_key",
                table: "commission_entries",
                column: "dedupe_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_commission_payouts_business_staff_paid",
                table: "commission_payouts",
                columns: new[] { "business_id", "staff_user_id", "paid_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_commission_payouts_staff_user_id",
                table: "commission_payouts",
                column: "staff_user_id");

            migrationBuilder.CreateIndex(
                name: "ux_commission_payouts_business_reference",
                table: "commission_payouts",
                columns: new[] { "business_id", "reference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_commission_rates_business_active",
                table: "commission_rates",
                columns: new[] { "business_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "IX_commission_rates_service_id",
                table: "commission_rates",
                column: "service_id");

            migrationBuilder.CreateIndex(
                name: "IX_commission_rates_staff_user_id",
                table: "commission_rates",
                column: "staff_user_id");

            migrationBuilder.CreateIndex(
                name: "ux_commission_rates_business_scope",
                table: "commission_rates",
                columns: new[] { "business_id", "scope_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_permissions_module",
                table: "permissions",
                column: "module_key");

            migrationBuilder.CreateIndex(
                name: "ux_permissions_code",
                table: "permissions",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_service_categories_business_active_order",
                table: "service_categories",
                columns: new[] { "business_id", "is_active", "sort_order" });

            migrationBuilder.CreateIndex(
                name: "ux_service_categories_business_name",
                table: "service_categories",
                columns: new[] { "business_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_staff_position_history_business_staff_changed",
                table: "staff_position_history",
                columns: new[] { "business_id", "staff_user_id", "changed_at" });

            migrationBuilder.CreateIndex(
                name: "IX_staff_position_history_changed_by_user_id",
                table: "staff_position_history",
                column: "changed_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_staff_position_history_position_id",
                table: "staff_position_history",
                column: "position_id");

            migrationBuilder.CreateIndex(
                name: "IX_staff_position_history_staff_user_id",
                table: "staff_position_history",
                column: "staff_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_staff_positions_business_active_order",
                table: "staff_positions",
                columns: new[] { "business_id", "is_active", "sort_order" });

            migrationBuilder.CreateIndex(
                name: "ux_staff_positions_business_name",
                table: "staff_positions",
                columns: new[] { "business_id", "name" },
                unique: true);


            migrationBuilder.AddForeignKey(
                name: "FK_business_memberships_staff_positions_position_id",
                table: "business_memberships",
                column: "position_id",
                principalTable: "staff_positions",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_reschedule_requests_appointments_appointment_id",
                table: "reschedule_requests",
                column: "appointment_id",
                principalTable: "appointments",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_reschedule_requests_businesses_business_id",
                table: "reschedule_requests",
                column: "business_id",
                principalTable: "businesses",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_reschedule_requests_users_original_staff_user_id",
                table: "reschedule_requests",
                column: "original_staff_user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_reschedule_requests_users_proposed_staff_user_id",
                table: "reschedule_requests",
                column: "proposed_staff_user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_reschedule_requests_users_requested_by_user_id",
                table: "reschedule_requests",
                column: "requested_by_user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_reschedule_requests_users_resolved_by_user_id",
                table: "reschedule_requests",
                column: "resolved_by_user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_services_service_categories_category_id",
                table: "services",
                column: "category_id",
                principalTable: "service_categories",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_business_memberships_staff_positions_position_id",
                table: "business_memberships");

            migrationBuilder.DropForeignKey(
                name: "FK_reschedule_requests_appointments_appointment_id",
                table: "reschedule_requests");

            migrationBuilder.DropForeignKey(
                name: "FK_reschedule_requests_businesses_business_id",
                table: "reschedule_requests");

            migrationBuilder.DropForeignKey(
                name: "FK_reschedule_requests_users_original_staff_user_id",
                table: "reschedule_requests");

            migrationBuilder.DropForeignKey(
                name: "FK_reschedule_requests_users_proposed_staff_user_id",
                table: "reschedule_requests");

            migrationBuilder.DropForeignKey(
                name: "FK_reschedule_requests_users_requested_by_user_id",
                table: "reschedule_requests");

            migrationBuilder.DropForeignKey(
                name: "FK_reschedule_requests_users_resolved_by_user_id",
                table: "reschedule_requests");

            migrationBuilder.DropForeignKey(
                name: "FK_services_service_categories_category_id",
                table: "services");

            migrationBuilder.DropTable(
                name: "business_membership_role_assignments");

            migrationBuilder.DropTable(
                name: "business_role_permissions");

            migrationBuilder.DropTable(
                name: "commission_configs");

            migrationBuilder.DropTable(
                name: "commission_entries");

            migrationBuilder.DropTable(
                name: "commission_payouts");

            migrationBuilder.DropTable(
                name: "commission_rates");

            migrationBuilder.DropTable(
                name: "permissions");

            migrationBuilder.DropTable(
                name: "service_categories");

            migrationBuilder.DropTable(
                name: "staff_position_history");

            migrationBuilder.DropTable(
                name: "business_roles");

            migrationBuilder.DropTable(
                name: "staff_positions");

            migrationBuilder.DropIndex(
                name: "ix_services_business_category",
                table: "services");

            migrationBuilder.DropIndex(
                name: "IX_services_category_id",
                table: "services");

            migrationBuilder.DropIndex(
                name: "ix_business_memberships_business_position",
                table: "business_memberships");

            migrationBuilder.DropIndex(
                name: "IX_business_memberships_position_id",
                table: "business_memberships");

            migrationBuilder.DropPrimaryKey(
                name: "PK_reschedule_requests",
                table: "reschedule_requests");

            migrationBuilder.DropIndex(
                name: "ix_reschedule_requests_appointment_status",
                table: "reschedule_requests");

            migrationBuilder.DropIndex(
                name: "ix_reschedule_requests_business_status_created",
                table: "reschedule_requests");

            migrationBuilder.DropIndex(
                name: "IX_reschedule_requests_original_staff_user_id",
                table: "reschedule_requests");

            migrationBuilder.DropIndex(
                name: "IX_reschedule_requests_proposed_staff_user_id",
                table: "reschedule_requests");

            migrationBuilder.DropIndex(
                name: "IX_reschedule_requests_requested_by_user_id",
                table: "reschedule_requests");

            migrationBuilder.DropIndex(
                name: "IX_reschedule_requests_resolved_by_user_id",
                table: "reschedule_requests");

            migrationBuilder.DropIndex(
                name: "ix_reschedule_requests_status_expires",
                table: "reschedule_requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_reschedule_requests_party",
                table: "reschedule_requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_reschedule_requests_status",
                table: "reschedule_requests");

            migrationBuilder.DropCheckConstraint(
                name: "ck_reschedule_requests_window",
                table: "reschedule_requests");

            migrationBuilder.DropColumn(
                name: "category_id",
                table: "services");

            migrationBuilder.DropColumn(
                name: "position_id",
                table: "business_memberships");

            migrationBuilder.DropColumn(
                name: "version",
                table: "appointments");

            migrationBuilder.DropColumn(
                name: "business_id",
                table: "reschedule_requests");

            migrationBuilder.DropColumn(
                name: "decision_notes",
                table: "reschedule_requests");

            migrationBuilder.DropColumn(
                name: "expires_at",
                table: "reschedule_requests");

            migrationBuilder.DropColumn(
                name: "original_end_at",
                table: "reschedule_requests");

            migrationBuilder.DropColumn(
                name: "original_scheduled_at",
                table: "reschedule_requests");

            migrationBuilder.DropColumn(
                name: "original_staff_user_id",
                table: "reschedule_requests");

            migrationBuilder.DropColumn(
                name: "request_reason",
                table: "reschedule_requests");

            migrationBuilder.DropColumn(
                name: "requested_by_party",
                table: "reschedule_requests");

            migrationBuilder.DropColumn(
                name: "updated_at",
                table: "reschedule_requests");

            migrationBuilder.DropColumn(
                name: "version",
                table: "reschedule_requests");

            migrationBuilder.RenameTable(
                name: "reschedule_requests",
                newName: "RescheduleRequests");

            migrationBuilder.RenameColumn(
                name: "showcase",
                table: "services",
                newName: "Showcase");

            migrationBuilder.RenameColumn(
                name: "description",
                table: "services",
                newName: "Description");

            migrationBuilder.RenameColumn(
                name: "status",
                table: "RescheduleRequests",
                newName: "Status");

            migrationBuilder.RenameColumn(
                name: "id",
                table: "RescheduleRequests",
                newName: "Id");

            migrationBuilder.RenameColumn(
                name: "resolved_by_user_id",
                table: "RescheduleRequests",
                newName: "ResolvedByUserId");

            migrationBuilder.RenameColumn(
                name: "resolved_at",
                table: "RescheduleRequests",
                newName: "ResolvedAt");

            migrationBuilder.RenameColumn(
                name: "requested_by_user_id",
                table: "RescheduleRequests",
                newName: "RequestedByUserId");

            migrationBuilder.RenameColumn(
                name: "proposed_staff_user_id",
                table: "RescheduleRequests",
                newName: "ProposedStaffUserId");

            migrationBuilder.RenameColumn(
                name: "proposed_services_json",
                table: "RescheduleRequests",
                newName: "ProposedServicesJson");

            migrationBuilder.RenameColumn(
                name: "proposed_scheduled_at",
                table: "RescheduleRequests",
                newName: "ProposedScheduledAt");

            migrationBuilder.RenameColumn(
                name: "proposed_end_at",
                table: "RescheduleRequests",
                newName: "ProposedEndAt");

            migrationBuilder.RenameColumn(
                name: "created_at",
                table: "RescheduleRequests",
                newName: "CreatedAt");

            migrationBuilder.RenameColumn(
                name: "appointment_id",
                table: "RescheduleRequests",
                newName: "AppointmentId");

            migrationBuilder.AlterColumn<bool>(
                name: "Showcase",
                table: "services",
                type: "boolean",
                nullable: false,
                oldClrType: typeof(bool),
                oldType: "boolean",
                oldDefaultValue: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_RescheduleRequests",
                table: "RescheduleRequests",
                column: "Id");
        }
    }
}
