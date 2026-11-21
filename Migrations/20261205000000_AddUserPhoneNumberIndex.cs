using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PunchedApi.Migrations
{
    /// <summary>
    /// Adds the filtered phone-number index used by exact-match customer lookups
    /// (directory, manual M-PESA, public booking, stamp enrolment).
    ///
    /// <para><b>Idempotent on purpose.</b> The index may already exist on a database
    /// where it was created out of band (an operator ran the SQL manually, or a
    /// previous attempt created the index but the migration history row was not
    /// written). A plain <c>CREATE INDEX</c> then fails with 42P07 and, because
    /// migrations run at startup outside production, that failure takes the API
    /// down. <c>IF NOT EXISTS</c> makes the migration converge instead.</para>
    ///
    /// <para>On an empty or small table this is instantaneous. On a large existing
    /// table prefer running <c>CREATE INDEX CONCURRENTLY</c> manually first — the
    /// migration then becomes a no-op.</para>
    /// </summary>
    public partial class AddUserPhoneNumberIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS ix_users_phone_number " +
                "ON users (phone_number) WHERE phone_number IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_users_phone_number;");
        }
    }
}
