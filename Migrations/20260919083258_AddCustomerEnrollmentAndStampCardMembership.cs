using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PunchedApi.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerEnrollmentAndStampCardMembership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "customer_business_enrollments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "discovery"),
                    enrolled_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    left_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customer_business_enrollments", x => x.id);
                    table.ForeignKey(
                        name: "FK_customer_business_enrollments_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_customer_business_enrollments_users_customer_id",
                        column: x => x.customer_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "customer_stamp_cards",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    customer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stamp_card_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    joined_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    left_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customer_stamp_cards", x => x.id);
                    table.ForeignKey(
                        name: "FK_customer_stamp_cards_stamp_cards_stamp_card_id",
                        column: x => x.stamp_card_id,
                        principalTable: "stamp_cards",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_customer_stamp_cards_users_customer_id",
                        column: x => x.customer_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_customer_business_enrollments_business_id_status",
                table: "customer_business_enrollments",
                columns: new[] { "business_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_customer_business_enrollments_customer_id_business_id",
                table: "customer_business_enrollments",
                columns: new[] { "customer_id", "business_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_customer_business_enrollments_customer_id_status",
                table: "customer_business_enrollments",
                columns: new[] { "customer_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_customer_stamp_cards_customer_id_stamp_card_id",
                table: "customer_stamp_cards",
                columns: new[] { "customer_id", "stamp_card_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_customer_stamp_cards_customer_id_status",
                table: "customer_stamp_cards",
                columns: new[] { "customer_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_customer_stamp_cards_stamp_card_id",
                table: "customer_stamp_cards",
                column: "stamp_card_id");

            // Backfill: implicit relationships imply enrollment (idempotent).
            migrationBuilder.Sql(@"INSERT INTO customer_business_enrollments
                (id, customer_id, business_id, status, source, enrolled_at, left_at, updated_at, created_at)
                SELECT gen_random_uuid(), customer_id, business_id, 0, 'loyalty', MIN(enrolled_at), NULL, NOW(), NOW()
                FROM loyalty_cards GROUP BY customer_id, business_id
                ON CONFLICT DO NOTHING;");
            migrationBuilder.Sql(@"INSERT INTO customer_business_enrollments
                (id, customer_id, business_id, status, source, enrolled_at, left_at, updated_at, created_at)
                SELECT gen_random_uuid(), customer_id, business_id, 0, 'booking', MIN(created_at), NULL, NOW(), NOW()
                FROM appointments GROUP BY customer_id, business_id
                ON CONFLICT DO NOTHING;");
            migrationBuilder.Sql(@"INSERT INTO customer_business_enrollments
                (id, customer_id, business_id, status, source, enrolled_at, left_at, updated_at, created_at)
                SELECT gen_random_uuid(), referrer_id, business_id, 0, 'referral', MIN(created_at), NULL, NOW(), NOW()
                FROM referral_links GROUP BY referrer_id, business_id
                ON CONFLICT DO NOTHING;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "customer_business_enrollments");

            migrationBuilder.DropTable(
                name: "customer_stamp_cards");
        }
    }
}
