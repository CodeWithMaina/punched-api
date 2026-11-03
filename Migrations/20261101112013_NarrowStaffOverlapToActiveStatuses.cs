using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PunchedApi.Migrations
{
    /// <summary>
    /// Aligns the staff-overlap exclusion constraint with the reservation rule the
    /// application enforces: only <c>booked</c> and <c>confirmed</c> appointments
    /// hold a staff member's time.
    ///
    /// <para>The constraint originally excluded <c>status = 'cancelled'</c> only, so
    /// a <i>terminal</i> appointment (<c>completed</c>, <c>no_show</c>) kept
    /// reserving its interval at the database level even though the availability
    /// engine treats it as history. Re-creating the constraint with the narrower
    /// predicate makes the database and the application agree.</para>
    ///
    /// <para>This only ever <b>loosens</b> the constraint, so no existing row can
    /// violate it: the previous predicate rejected every overlap among non-cancelled
    /// rows, which is a superset of the overlaps rejected now. No backfill is needed,
    /// and no table, column, index or foreign key is dropped. The predicate text
    /// matches <c>AppointmentReservations.ReservationPredicateSql</c>.</para>
    /// </summary>
    public partial class NarrowStaffOverlapToActiveStatuses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // btree_gist provides the equality operator class for uuid. It is already
            // installed by 20260823181450; the guard keeps this migration runnable on a
            // database where the extension was created out of band.
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS btree_gist;");

            migrationBuilder.Sql(
                "ALTER TABLE appointments DROP CONSTRAINT IF EXISTS appointments_no_staff_overlap;");

            migrationBuilder.Sql(
                """
                ALTER TABLE appointments
                ADD CONSTRAINT appointments_no_staff_overlap
                EXCLUDE USING gist (
                    staff_user_id WITH =,
                    tstzrange(scheduled_at, end_at) WITH &&
                )
                WHERE (status IN ('booked', 'confirmed'));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER TABLE appointments DROP CONSTRAINT IF EXISTS appointments_no_staff_overlap;");

            // Re-adding the WIDER predicate is not symmetric: an interval released by
            // the new rule may since have been booked again, so overlapping terminal
            // rows are normalised first (marked cancelled and collapsed to a
            // zero-length interval) rather than aborting the rollback. The rows
            // themselves are preserved — only their released intervals are cleared.
            migrationBuilder.Sql(
                """
                WITH overlapping AS (
                    SELECT a.id
                    FROM appointments a
                    WHERE a.status NOT IN ('booked', 'confirmed')
                      AND a.staff_user_id IS NOT NULL
                      AND EXISTS (
                          SELECT 1
                          FROM appointments other
                          WHERE other.id <> a.id
                            AND other.staff_user_id = a.staff_user_id
                            AND tstzrange(other.scheduled_at, other.end_at)
                                && tstzrange(a.scheduled_at, a.end_at)
                      )
                )
                UPDATE appointments
                SET status = 'cancelled',
                    end_at = scheduled_at,
                    version = version + 1
                WHERE id IN (SELECT id FROM overlapping);
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE appointments
                ADD CONSTRAINT appointments_no_staff_overlap
                EXCLUDE USING gist (
                    staff_user_id WITH =,
                    tstzrange(scheduled_at, end_at) WITH &&
                )
                WHERE (status <> 'cancelled');
                """);
        }
    }
}
