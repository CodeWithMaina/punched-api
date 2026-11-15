using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PunchedApi.Infrastructure.Data;

#nullable disable

namespace PunchedApi.Migrations;

/// <summary>
/// Indexes for the appointment list, calendar and dashboard query shapes.
///
/// <para><b>Why these two and no others.</b> The appointments screen filters and counts by
/// <c>business_id</c> and then orders by <c>scheduled_at</c>, <c>created_at</c> or
/// <c>status</c>. <c>(business_id, scheduled_at)</c> already existed for the calendar; these add
/// the two orderings that had none, so "newest first" and a status-filtered count are index
/// scans per business instead of a full sort of the table.</para>
///
/// <para>Deliberately NOT added: single-column indexes on <c>status</c> or <c>created_at</c>
/// (every query is tenant-scoped, so the composite is the useful one — a second, narrower index
/// would only cost write throughput), and payment/service/staff relationships (already indexed
/// by the appointments, payments and appointment_resources configurations).</para>
///
/// <para>Additive and safe to apply to a populated database: two indexes, no data change.</para>
/// </summary>
[DbContext(typeof(ApplicationDbContext))]
[Migration("20261203000001_AddAppointmentListIndexes")]
public partial class AddAppointmentListIndexes : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "IX_appointments_business_id_created_at",
            table: "appointments",
            columns: new[] { "business_id", "created_at" });

        migrationBuilder.CreateIndex(
            name: "IX_appointments_business_id_status",
            table: "appointments",
            columns: new[] { "business_id", "status" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_appointments_business_id_created_at",
            table: "appointments");

        migrationBuilder.DropIndex(
            name: "IX_appointments_business_id_status",
            table: "appointments");
    }
}
