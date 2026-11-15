using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PunchedApi.Migrations
{
    /// <inheritdoc />
    public partial class AddSendMoneyMpesaRoute : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MpesaPhoneNumber",
                table: "business_payment_configs",
                type: "character varying(12)",
                maxLength: 12,
                nullable: true);

            // ── Reconcile legacy manual rows with the new invariant ──────────────
            // The new CHECK constraint requires the SELECTED method to be complete when
            // manual M-PESA is on. Rows written before the method selector existed stored
            // the column's default "paybill" even when the business collected on a Till,
            // so they are aligned to the method the pre-migration resolver would have
            // reported (paybill when complete, else till). This invents no data and
            // changes no public behaviour — it only makes the stored type agree with the
            // fields the row already had.

            // A manual channel with no complete PayBill or Till could never be paid; the
            // old storefront already reported nothing for it. Switch it off so a broken
            // row cannot block the constraint.
            migrationBuilder.Sql(
                """
                UPDATE business_payment_configs
                SET "ManualMpesaEnabled" = false
                WHERE "ManualMpesaEnabled" = true
                  AND NOT (
                      (btrim(coalesce("MpesaPaybillNumber", '')) <> ''
                           AND btrim(coalesce("MpesaPaybillAccount", '')) <> '')
                      OR btrim(coalesce("MpesaTillNumber", '')) <> ''
                  );
                """);

            // Align the stored method with the single complete method the row actually has.
            migrationBuilder.Sql(
                """
                UPDATE business_payment_configs
                SET "MpesaAccountType" =
                    CASE
                        WHEN btrim(coalesce("MpesaPaybillNumber", '')) <> ''
                             AND btrim(coalesce("MpesaPaybillAccount", '')) <> '' THEN 'paybill'
                        WHEN btrim(coalesce("MpesaTillNumber", '')) <> '' THEN 'till'
                        ELSE "MpesaAccountType"
                    END
                WHERE "ManualMpesaEnabled" = true
                  AND NOT (
                      ("MpesaAccountType" = 'paybill'
                           AND btrim(coalesce("MpesaPaybillNumber", '')) <> ''
                           AND btrim(coalesce("MpesaPaybillAccount", '')) <> '')
                      OR ("MpesaAccountType" = 'till'
                           AND btrim(coalesce("MpesaTillNumber", '')) <> '')
                      OR ("MpesaAccountType" = 'send_money'
                           AND btrim(coalesce("MpesaPhoneNumber", '')) <> '')
                  );
                """);

            // ── DB-level CHECK constraints (PostgreSQL) ──────────────────────────
            // 1. The account type is one of the three known routes.
            migrationBuilder.Sql(
                """
                ALTER TABLE business_payment_configs
                ADD CONSTRAINT ck_business_payment_configs_account_type
                CHECK ("MpesaAccountType" IN ('paybill', 'till', 'send_money'));
                """);

            // 2. When manual M-PESA is on, the selected method's own fields are complete.
            migrationBuilder.Sql(
                """
                ALTER TABLE business_payment_configs
                ADD CONSTRAINT ck_business_payment_configs_manual_method_complete
                CHECK (
                    NOT "ManualMpesaEnabled"
                    OR ("MpesaAccountType" = 'paybill'
                        AND btrim(coalesce("MpesaPaybillNumber", '')) <> ''
                        AND btrim(coalesce("MpesaPaybillAccount", '')) <> '')
                    OR ("MpesaAccountType" = 'till'
                        AND btrim(coalesce("MpesaTillNumber", '')) <> '')
                    OR ("MpesaAccountType" = 'send_money'
                        AND btrim(coalesce("MpesaPhoneNumber", '')) <> '')
                );
                """);

            // 3. Any stored phone is the normalised 254XXXXXXXXX form (or null).
            migrationBuilder.Sql(
                """
                ALTER TABLE business_payment_configs
                ADD CONSTRAINT ck_business_payment_configs_phone_format
                CHECK ("MpesaPhoneNumber" IS NULL OR "MpesaPhoneNumber" ~ '^254[71][0-9]{8}$');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER TABLE business_payment_configs DROP CONSTRAINT IF EXISTS ck_business_payment_configs_phone_format;");
            migrationBuilder.Sql(
                "ALTER TABLE business_payment_configs DROP CONSTRAINT IF EXISTS ck_business_payment_configs_manual_method_complete;");
            migrationBuilder.Sql(
                "ALTER TABLE business_payment_configs DROP CONSTRAINT IF EXISTS ck_business_payment_configs_account_type;");

            migrationBuilder.DropColumn(
                name: "MpesaPhoneNumber",
                table: "business_payment_configs");
        }
    }
}
