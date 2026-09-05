using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PunchedApi.Migrations
{
    /// <inheritdoc />
    public partial class AddStampCardsAndCardDesigns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "card_designs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, defaultValue: "Card Design"),
                    html_template = table.Column<string>(type: "character varying(50000)", maxLength: 50000, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_card_designs", x => x.id);
                    table.ForeignKey(
                        name: "FK_card_designs_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "stamp_cards",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    program_id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, defaultValue: "Stamp Card"),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    stamps_required = table.Column<int>(type: "integer", nullable: false),
                    reward_description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    reward_value = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    card_design_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stamp_cards", x => x.id);
                    table.CheckConstraint("chk_stamp_card_stamps_required_positive", "\"stamps_required\" > 0");
                    table.ForeignKey(
                        name: "FK_stamp_cards_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_stamp_cards_card_designs_card_design_id",
                        column: x => x.card_design_id,
                        principalTable: "card_designs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_stamp_cards_loyalty_programs_program_id",
                        column: x => x.program_id,
                        principalTable: "loyalty_programs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_card_designs_business_id",
                table: "card_designs",
                column: "business_id");

            migrationBuilder.CreateIndex(
                name: "IX_stamp_cards_business_id",
                table: "stamp_cards",
                column: "business_id");

            migrationBuilder.CreateIndex(
                name: "IX_stamp_cards_card_design_id",
                table: "stamp_cards",
                column: "card_design_id");

            migrationBuilder.CreateIndex(
                name: "IX_stamp_cards_program_id",
                table: "stamp_cards",
                column: "program_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "stamp_cards");

            migrationBuilder.DropTable(
                name: "card_designs");
        }
    }
}
