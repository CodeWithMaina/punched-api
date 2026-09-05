using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PunchedApi.Migrations
{
    /// <summary>
    /// Adds stamps.unlocked_at — supports the locked/pending welcome-stamp model:
    /// enrollment ("welcome") stamps start locked (unlocked_at IS NULL) and are
    /// unlocked by the customer's first verified business stamping action.
    /// Existing rows are backfilled as unlocked to preserve current behaviour.
    /// </summary>
    [Migration("20260904100000_AddStampUnlockedAt")]
    public partial class AddStampUnlockedAt : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "unlocked_at",
                table: "stamps",
                type: "timestamp with time zone",
                nullable: true);

            // Legacy stamps (including all previously-granted welcome stamps) are
            // considered verified — only new enrollments create locked stamps.
            migrationBuilder.Sql(
                "UPDATE stamps SET unlocked_at = stamped_at WHERE unlocked_at IS NULL;");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "unlocked_at",
                table: "stamps");
        }
    }
}
