using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PunchedApi.Infrastructure.Data;

#nullable disable

namespace PunchedApi.Migrations;

/// <summary>
/// Supports the repeatable win-back cooldown: the newest successfully sent
/// win-back per (business, customer) is read before every nudge. Partial on
/// <c>status = 'sent'</c> so the index only covers delivered rows.
/// Additive only — no data rewrite.
/// </summary>
[DbContext(typeof(ApplicationDbContext))]
[Migration("20261201000000_AddWinBackCooldownIndex")]
public partial class AddWinBackCooldownIndex : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "ix_notifications_winback_cooldown",
            table: "notifications",
            columns: new[] { "business_id", "user_id", "template_type", "status", "sent_at" },
            filter: "status = 'sent'");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ix_notifications_winback_cooldown",
            table: "notifications");
    }
}
