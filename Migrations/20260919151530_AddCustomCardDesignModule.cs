using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PunchedApi.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomCardDesignModule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "card_design_id",
                table: "loyalty_programs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "business_id",
                table: "card_designs",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<bool>(
                name: "is_default",
                table: "card_designs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_programs_card_design_id",
                table: "loyalty_programs",
                column: "card_design_id");

            migrationBuilder.CreateIndex(
                name: "ux_card_designs_single_default",
                table: "card_designs",
                column: "is_default",
                unique: true,
                filter: "\"is_default\" = TRUE");

            migrationBuilder.AddCheckConstraint(
                name: "ck_card_designs_default_is_system",
                table: "card_designs",
                sql: "\"is_default\" = FALSE OR \"business_id\" IS NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_loyalty_programs_card_designs_card_design_id",
                table: "loyalty_programs",
                column: "card_design_id",
                principalTable: "card_designs",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_loyalty_programs_card_designs_card_design_id",
                table: "loyalty_programs");

            migrationBuilder.DropIndex(
                name: "IX_loyalty_programs_card_design_id",
                table: "loyalty_programs");

            migrationBuilder.DropIndex(
                name: "ux_card_designs_single_default",
                table: "card_designs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_card_designs_default_is_system",
                table: "card_designs");

            migrationBuilder.DropColumn(
                name: "card_design_id",
                table: "loyalty_programs");

            migrationBuilder.DropColumn(
                name: "is_default",
                table: "card_designs");

            migrationBuilder.AlterColumn<Guid>(
                name: "business_id",
                table: "card_designs",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
