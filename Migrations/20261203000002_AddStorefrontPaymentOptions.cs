using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PunchedApi.Migrations
{
    /// <summary>
    /// Adds the two storefront payment switches to <c>business_payment_configs</c>.
    ///
    /// <para><b>Payment Included is on, Pay After Service is off — for every existing row.</b>
    /// That is not a guess about what businesses want: the column defaults are the behaviour
    /// every storefront already had (a customer books and pays during booking), so applying
    /// this migration changes nothing about what a live booking page does. Pay After Service
    /// changes what a customer is asked for up front and immediately creates an outstanding
    /// payment, so a migration must never switch it on by itself — the owner does that.</para>
    ///
    /// <para><b>Why not null.</b> Both are non-nullable booleans with a server-side default, so
    /// existing rows are backfilled by the <c>DEFAULT</c> in the same statement that adds the
    /// column — no UPDATE pass, no window in which a row has no value, and no NULL to handle in
    /// the booking path. A business created after this migration gets the same values from the
    /// entity's own defaults.</para>
    ///
    /// <para><b>Column names are the property names.</b> This table predates the explicit
    /// snake_case mapping the rest of the schema uses (see the <c>CreateTable</c> in
    /// <c>AddPaymentsModule</c>), so the columns must be <c>PayAfterServiceEnabled</c> and
    /// <c>PayUpfrontEnabled</c> — not snake_case. Getting that wrong makes the model query a
    /// column that does not exist, which is a 500 on every read of the business's payment
    /// setup.</para>
    ///
    /// <para>Additive and safe against a populated database: two columns, no data rewrite, and
    /// nothing dropped in <c>Down</c> except what this migration added.</para>
    /// </summary>
    public partial class AddStorefrontPaymentOptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "PayAfterServiceEnabled",
                table: "business_payment_configs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "PayUpfrontEnabled",
                table: "business_payment_configs",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PayAfterServiceEnabled",
                table: "business_payment_configs");

            migrationBuilder.DropColumn(
                name: "PayUpfrontEnabled",
                table: "business_payment_configs");
        }
    }
}
