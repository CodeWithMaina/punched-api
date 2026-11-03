using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PunchedApi.Infrastructure.Data;

#nullable disable

namespace PunchedApi.Migrations;

/// <summary>
/// Adds the business's working note to an appointment so the Appointments
/// module can persist preparation instructions, allergies and payment remarks
/// against the booking itself (rather than only inside the status history).
///
/// Nullable and additive: existing rows keep <c>null</c>, no data rewrite, and
/// <see cref="Down"/> simply drops the column.
/// </summary>
[DbContext(typeof(ApplicationDbContext))]
[Migration("20261202000000_AddAppointmentNote")]
public partial class AddAppointmentNote : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "note",
            table: "appointments",
            type: "character varying(500)",
            maxLength: 500,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "note",
            table: "appointments");
    }
}
