using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PunchedApi.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Contract step of the canonical auth refactor: <c>users.StaffBusinessId</c>
    /// is no longer read or written by any code path, so the legacy column and
    /// its index are removed.
    ///
    /// All staff relationships now live in <c>business_memberships</c>; the
    /// backfill in <c>AddBusinessMemberships</c> already copied every non-null
    /// pointer into a Staff membership, so this migration loses no data.
    /// Rollback re-adds the column (empty) and its index — the canonical
    /// memberships keep working either way.
    /// </summary>
    public partial class RemoveLegacyStaffBusinessId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_users_StaffBusinessId",
                table: "users");

            migrationBuilder.DropColumn(
                name: "StaffBusinessId",
                table: "users");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "StaffBusinessId",
                table: "users",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_StaffBusinessId",
                table: "users",
                column: "StaffBusinessId");
        }
    }
}
