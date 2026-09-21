using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data.Configurations;

/// <summary>
/// Maps <see cref="AttendanceEvent"/> to <c>attendance_events</c>.
/// Append-only ledger (the Attendance counterpart of <c>stamps</c>).
/// The shared location QR is legitimately reused, so the credential FK is
/// a plain indexed FK — deliberately NOT unique.
/// </summary>
public class AttendanceEventConfiguration : IEntityTypeConfiguration<AttendanceEvent>
{
    public void Configure(EntityTypeBuilder<AttendanceEvent> builder)
    {
        builder.ToTable("attendance_events");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.BusinessId).HasColumnName("business_id");
        builder.Property(x => x.StaffUserId).HasColumnName("staff_user_id");
        builder.Property(x => x.EventType)
            .HasColumnName("event_type")
            .HasConversion<string>()
            .HasMaxLength(30);
        builder.Property(x => x.OccurredAt).HasColumnName("occurred_at");
        builder.Property(x => x.AttendanceLocationId).HasColumnName("attendance_location_id");
        builder.Property(x => x.AttendanceQrCredentialId).HasColumnName("attendance_qr_credential_id");
        builder.Property(x => x.AttendanceSessionId).HasColumnName("attendance_session_id");
        builder.Property(x => x.Source)
            .HasColumnName("source")
            .HasConversion<string>()
            .HasMaxLength(20);
        builder.Property(x => x.VerificationSummaryJson).HasColumnName("verification_summary_json");
        builder.Property(x => x.ClientIdempotencyKey).HasColumnName("client_idempotency_key").HasMaxLength(200);
        builder.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id");
        builder.Property(x => x.RecordedAt).HasColumnName("recorded_at");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");

        builder.ToTable(t =>
        {
            t.HasCheckConstraint(
                "chk_attendance_events_clockout_requires_session",
                "\"event_type\" <> 'ClockOut' OR \"attendance_session_id\" IS NOT NULL");
        });

        builder.HasIndex(x => new { x.BusinessId, x.OccurredAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_attendance_events_business_occurred");

        builder.HasIndex(x => new { x.StaffUserId, x.OccurredAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_attendance_events_staff_occurred");

        builder.HasIndex(x => new { x.AttendanceLocationId, x.OccurredAt })
            .HasDatabaseName("ix_attendance_events_location_occurred");

        builder.HasIndex(x => x.AttendanceSessionId)
            .HasDatabaseName("ix_attendance_events_session");

        builder.HasIndex(x => x.AttendanceQrCredentialId)
            .HasDatabaseName("ix_attendance_events_qr_credential");

        // DB-level replay protection (PostgreSQL treats NULLs as distinct,
        // so non-idempotent writes are unaffected).
        builder.HasIndex(x => new { x.BusinessId, x.StaffUserId, x.ClientIdempotencyKey })
            .IsUnique()
            .HasDatabaseName("ix_attendance_events_idempotency");

        builder.HasOne<Business>()
            .WithMany()
            .HasForeignKey(x => x.BusinessId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.StaffUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<AttendanceLocation>()
            .WithMany()
            .HasForeignKey(x => x.AttendanceLocationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<AttendanceQrCredential>()
            .WithMany()
            .HasForeignKey(x => x.AttendanceQrCredentialId)
            .OnDelete(DeleteBehavior.SetNull);

        // With sessions.opening/closing_event_id → attendance_events this is a
        // deliberate FK cycle; both sides are Restrict so there is no cascade
        // path. The migration scaffolder defers this constraint until both
        // tables exist.
        builder.HasOne<AttendanceSession>()
            .WithMany()
            .HasForeignKey(x => x.AttendanceSessionId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
