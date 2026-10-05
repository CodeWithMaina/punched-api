using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PunchedApi.Migrations
{
    /// <inheritdoc />
    public partial class AllowMultipleLoyaltyProgramsPerCustomer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_loyalty_cards_customer_id_business_id",
                table: "loyalty_cards");

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_cards_customer_id_program_id",
                table: "loyalty_cards",
                columns: new[] { "customer_id", "program_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_loyalty_cards_customer_id_program_id",
                table: "loyalty_cards");

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_cards_customer_id_business_id",
                table: "loyalty_cards",
                columns: new[] { "customer_id", "business_id" },
                unique: true);
        }
    }
}
