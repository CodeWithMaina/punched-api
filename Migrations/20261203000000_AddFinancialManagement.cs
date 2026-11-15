using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PunchedApi.Infrastructure.Data;

#nullable disable

namespace PunchedApi.Migrations;

/// <summary>
/// Adds the Financial Management module: the business ledger, its audit trail, and M-Pesa statement
/// reconciliation.
///
/// <para><b>Additive only.</b> Four new tables; nothing existing is dropped, renamed or rewritten, so
/// the migration is safe against a populated production database and every current booking and payment
/// flow keeps working unchanged.</para>
///
/// <para><b>The two guarantees the schema enforces.</b>
/// <c>IX_financial_transactions_source_payment_id</c> is unique, so a settled payment can be recognised
/// exactly once even when a provider callback is replayed; and
/// <c>IX_mpesa_statement_imports_business_id_content_hash</c> is unique per business, so re-uploading the
/// same statement cannot duplicate every line in it. Both are database constraints rather than service
/// checks because both are races a service cannot win on its own.</para>
///
/// <para><b>Column naming.</b> Every column is snake_case, matching the model's explicit
/// <c>HasColumnName</c> mappings and the rest of this schema. The migration is hand-written (like
/// <c>AddWinBackCooldownIndex</c> and <c>AddAppointmentNote</c>) so the ordering and names stay
/// reviewable; the DDL below was verified against <c>dotnet ef dbcontext script</c>.</para>
/// </summary>
[DbContext(typeof(ApplicationDbContext))]
[Migration("20261203000000_AddFinancialManagement")]
public partial class AddFinancialManagement : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // ── The business ledger ──────────────────────────────────
        migrationBuilder.CreateTable(
            name: "financial_transactions",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                business_id = table.Column<Guid>(type: "uuid", nullable: false),
                type = table.Column<int>(type: "integer", nullable: false),
                source = table.Column<int>(type: "integer", nullable: false),
                payment_method = table.Column<int>(type: "integer", nullable: false),
                verification_status = table.Column<int>(type: "integer", nullable: false),
                reconciliation_status = table.Column<int>(type: "integer", nullable: false),
                amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                reference = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                external_reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                category = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                customer_id = table.Column<Guid>(type: "uuid", nullable: true),
                customer_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                appointment_id = table.Column<Guid>(type: "uuid", nullable: true),
                source_payment_id = table.Column<Guid>(type: "uuid", nullable: true),
                refund_of_transaction_id = table.Column<Guid>(type: "uuid", nullable: true),
                refund_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                refund_reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                transaction_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                recorded_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                verified_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                verified_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                rejection_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                created_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                is_voided = table.Column<bool>(type: "boolean", nullable: false),
                voided_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                voided_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                void_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                reconciled_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                reconciled_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                statement_line_id = table.Column<Guid>(type: "uuid", nullable: true),
                reconciliation_note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_financial_transactions", x => x.id);
                // Column names here are snake_case to match the model's explicit HasColumnName
                // mappings and the rest of the schema. The FK targets businesses.id — the previous
                // version referenced businesses."Id", which does not exist and made every
                // `dotnet ef database update` fail with 42703.
                table.ForeignKey(
                    name: "FK_financial_transactions_businesses_business_id",
                    column: x => x.business_id,
                    principalTable: "businesses",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        // The ledger reference is unique, and so is the payment a row recognises: the second constraint
        // is what makes "a settled payment is booked exactly once" a database fact rather than a hope.
        migrationBuilder.CreateIndex(
            name: "IX_financial_transactions_reference",
            table: "financial_transactions",
            column: "reference",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_financial_transactions_source_payment_id",
            table: "financial_transactions",
            column: "source_payment_id",
            unique: true);

        // Query patterns: the owner's dated ledger list, the overview/analytics aggregates (which always
        // pin the type and verification state), expense reporting by category, appointment and customer
        // drill-downs, refunds against a row, and the unreconciled work queue.
        migrationBuilder.CreateIndex(
            name: "IX_financial_transactions_business_id_transaction_date",
            table: "financial_transactions",
            columns: new[] { "business_id", "transaction_date" });

        migrationBuilder.CreateIndex(
            name: "IX_financial_transactions_business_id_type_verification_status",
            table: "financial_transactions",
            columns: new[] { "business_id", "type", "verification_status", "transaction_date" });

        migrationBuilder.CreateIndex(
            name: "IX_financial_transactions_business_id_category_transaction_date",
            table: "financial_transactions",
            columns: new[] { "business_id", "category", "transaction_date" });

        migrationBuilder.CreateIndex(
            name: "IX_financial_transactions_business_id_reconciliation_status",
            table: "financial_transactions",
            columns: new[] { "business_id", "reconciliation_status" });

        migrationBuilder.CreateIndex(
            name: "IX_financial_transactions_appointment_id",
            table: "financial_transactions",
            column: "appointment_id");

        migrationBuilder.CreateIndex(
            name: "IX_financial_transactions_customer_id",
            table: "financial_transactions",
            column: "customer_id");

        migrationBuilder.CreateIndex(
            name: "IX_financial_transactions_refund_of_transaction_id",
            table: "financial_transactions",
            column: "refund_of_transaction_id");

        // ── Append-only audit trail ──────────────────────────────
        migrationBuilder.CreateTable(
            name: "financial_audit_logs",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                business_id = table.Column<Guid>(type: "uuid", nullable: false),
                entity_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                action = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                actor_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                changes_json = table.Column<string>(type: "text", nullable: true),
                reference = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_financial_audit_logs", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_financial_audit_logs_business_id_created_at",
            table: "financial_audit_logs",
            columns: new[] { "business_id", "created_at" });

        migrationBuilder.CreateIndex(
            name: "IX_financial_audit_logs_entity_type_entity_id",
            table: "financial_audit_logs",
            columns: new[] { "entity_type", "entity_id" });

        // ── M-Pesa statement imports ─────────────────────────────
        // The batch exists so an import has its own identity, and the content hash is unique per
        // business: re-uploading the same statement cannot duplicate every line in it.
        migrationBuilder.CreateTable(
            name: "mpesa_statement_imports",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                business_id = table.Column<Guid>(type: "uuid", nullable: false),
                file_name = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                format = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                line_count = table.Column<int>(type: "integer", nullable: false),
                matched_count = table.Column<int>(type: "integer", nullable: false),
                discrepancy_count = table.Column<int>(type: "integer", nullable: false),
                unmatched_count = table.Column<int>(type: "integer", nullable: false),
                excluded_count = table.Column<int>(type: "integer", nullable: false),
                imported_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                imported_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_mpesa_statement_imports", x => x.id);
                table.ForeignKey(
                    name: "FK_mpesa_statement_imports_businesses_business_id",
                    column: x => x.business_id,
                    principalTable: "businesses",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_mpesa_statement_imports_business_id_content_hash",
            table: "mpesa_statement_imports",
            columns: new[] { "business_id", "content_hash" },
            unique: true);

        // ── Imported statement lines ─────────────────────────────
        // The receipt index is deliberately NOT unique: the same receipt legitimately reappears when a
        // payment was reversed and re-sent, and a genuinely duplicated receipt is a finding the
        // reconciler must SEE rather than a constraint that aborts the import.
        migrationBuilder.CreateTable(
            name: "mpesa_statement_lines",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                business_id = table.Column<Guid>(type: "uuid", nullable: false),
                import_id = table.Column<Guid>(type: "uuid", nullable: false),
                receipt_number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                transaction_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                payer_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                payer_phone = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                account_reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                narrative = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                status = table.Column<int>(type: "integer", nullable: false),
                matched_transaction_id = table.Column<Guid>(type: "uuid", nullable: true),
                matched_payment_id = table.Column<Guid>(type: "uuid", nullable: true),
                match_method = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                is_duplicate_in_file = table.Column<bool>(type: "boolean", nullable: false),
                updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_mpesa_statement_lines", x => x.id);
                table.ForeignKey(
                    name: "FK_mpesa_statement_lines_mpesa_statement_imports_import_id",
                    column: x => x.import_id,
                    principalTable: "mpesa_statement_imports",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_mpesa_statement_lines_import_id_receipt_number",
            table: "mpesa_statement_lines",
            columns: new[] { "import_id", "receipt_number" });

        migrationBuilder.CreateIndex(
            name: "IX_mpesa_statement_lines_business_id_status",
            table: "mpesa_statement_lines",
            columns: new[] { "business_id", "status" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "financial_audit_logs");
        migrationBuilder.DropTable(name: "mpesa_statement_lines");
        migrationBuilder.DropTable(name: "mpesa_statement_imports");
        migrationBuilder.DropTable(name: "financial_transactions");
    }
}
