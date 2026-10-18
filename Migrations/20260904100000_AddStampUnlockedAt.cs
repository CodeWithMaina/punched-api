using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PunchedApi.Infrastructure.Data;

#nullable disable

namespace PunchedApi.Migrations
{
    /// <summary>
    /// Adds stamps.unlocked_at — supports the locked/pending welcome-stamp model:
    /// enrollment ("welcome") stamps start locked (unlocked_at IS NULL) and are
    /// unlocked by the customer's first verified business stamping action.
    /// Existing rows are backfilled as unlocked to preserve current behaviour.
    /// </summary>
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20260904100000_AddStampUnlockedAt")]
    public partial class AddStampUnlockedAt : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Idempotent: stamps.unlocked_at may already exist (added by the
            // EnsureStampUnlockedAtColumn / FixStampUnlockedAtColumn repair
            // migrations), so use IF NOT EXISTS to avoid a duplicate-column error
            // when this recovered migration is applied to an existing database.
            migrationBuilder.Sql(
                "ALTER TABLE stamps ADD COLUMN IF NOT EXISTS unlocked_at timestamp with time zone;");

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
