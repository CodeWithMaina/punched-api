using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PunchedApi.Migrations
{
    /// <inheritdoc />
    public partial class AddBusinessMemberships : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "business_memberships",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    business_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    invited_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    joined_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_business_memberships", x => x.id);
                    table.CheckConstraint("ck_business_memberships_role", "role IN ('Owner', 'Staff', 'Customer')");
                    table.CheckConstraint("ck_business_memberships_status", "status IN ('Active', 'Invited', 'Suspended', 'Left')");
                    table.ForeignKey(
                        name: "FK_business_memberships_businesses_business_id",
                        column: x => x.business_id,
                        principalTable: "businesses",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_business_memberships_users_invited_by_user_id",
                        column: x => x.invited_by_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_business_memberships_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_business_memberships_business_role_status",
                table: "business_memberships",
                columns: new[] { "business_id", "role", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_business_memberships_invited_by_user_id",
                table: "business_memberships",
                column: "invited_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_business_memberships_user_status_role",
                table: "business_memberships",
                columns: new[] { "user_id", "status", "role" });

            migrationBuilder.CreateIndex(
                name: "ux_business_memberships_single_owner",
                table: "business_memberships",
                column: "business_id",
                unique: true,
                filter: "role = 'Owner' AND status = 'Active'");

            migrationBuilder.CreateIndex(
                name: "ux_business_memberships_user_business",
                table: "business_memberships",
                columns: new[] { "user_id", "business_id" },
                unique: true);

            // ══════════════════════════════════════════════════════════════
            //  Backfill — legacy relationship sources → business_memberships
            //
            //  The three legacy signals are collapsed into the canonical table
            //  once, with upgrade-only precedence (Owner > Staff > Customer).
            //  After this migration nothing reads them for authorization.
            //
            //  1. businesses.owner_id            → Owner    / Active
            //  2. users."StaffBusinessId"        → Staff    / Active
            //  3. customer_business_enrollments  → Customer / Active | Left
            //
            //  ON CONFLICT (user_id, business_id) makes the script idempotent
            //  and guarantees the strongest role wins when one person appears
            //  in several legacy sources.
            // ══════════════════════════════════════════════════════════════

            migrationBuilder.Sql("""
                INSERT INTO business_memberships
                    (id, user_id, business_id, role, status, invited_by_user_id,
                     joined_at, ended_at, updated_at, created_at)
                SELECT gen_random_uuid(), b.owner_id, b.id, 'Owner', 'Active', NULL,
                       b.created_at, NULL, now(), now()
                FROM businesses b
                JOIN users u ON u.id = b.owner_id
                WHERE b.owner_id IS NOT NULL
                  AND b.owner_id <> '00000000-0000-0000-0000-000000000000'
                ON CONFLICT (user_id, business_id) DO NOTHING;
                """);

            migrationBuilder.Sql("""
                INSERT INTO business_memberships
                    (id, user_id, business_id, role, status, invited_by_user_id,
                     joined_at, ended_at, updated_at, created_at)
                SELECT gen_random_uuid(), u.id, u."StaffBusinessId", 'Staff', 'Active', NULL,
                       u.created_at, NULL, now(), now()
                FROM users u
                JOIN businesses b ON b.id = u."StaffBusinessId"
                WHERE u."StaffBusinessId" IS NOT NULL
                ON CONFLICT (user_id, business_id) DO NOTHING;
                """);

            migrationBuilder.Sql("""
                INSERT INTO business_memberships
                    (id, user_id, business_id, role, status, invited_by_user_id,
                     joined_at, ended_at, updated_at, created_at)
                SELECT gen_random_uuid(), e.customer_id, e.business_id, 'Customer',
                       CASE WHEN e.status = 0 THEN 'Active' ELSE 'Left' END,
                       NULL, e.enrolled_at,
                       CASE WHEN e.status = 0 THEN NULL ELSE e.left_at END,
                       now(), now()
                FROM customer_business_enrollments e
                JOIN users u ON u.id = e.customer_id
                JOIN businesses b ON b.id = e.business_id
                ON CONFLICT (user_id, business_id) DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "business_memberships");
        }
    }
}
