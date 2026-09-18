using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data.Configurations;

/// <summary>
/// Maps <see cref="AttendanceSession"/> to <c>attendance_sessions</c>.
/// The partial unique open-per-staff index is THE duplicate-clock-in
/// guarantee.
/// </summary>
public class AttendanceSessionConfiguration : IEntityTypeConfiguration<AttendanceSession>
{
    public void Configure(EntityTypeBuilder<AttendanceSession> builder)
    {
        builder.ToTable("attendance_sessions");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.BusinessId).HasColumnName("business_id");
        builder.Property(x => x.StaffUserId).HasColumnName("staff_user_id");
        builder.Property(x => x.OpeningEventId).HasColumnName("opening_event_id");
        builder.Property(x => x.ClosingEventId).HasColumnName("closing_event_id");
        builder.Property(x => x.OpenedAt).HasColumnName("opened_at");
        builder.Property(x => x.ClosedAt).HasColumnName("closed_at");
        builder.Property(x => x.OpeningLocationId).HasColumnName("opening_location_id");
        builder.Property(x => x.ClosingLocationId).HasColumnName("closing_location_id");
        builder.Property(x => x.WorkedMinutes).HasColumnName("worked_minutes");
        builder.Property(x => x.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(20);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");

        builder.ToTable(t =>
        {
            t.HasCheckConstraint(
                "chk_attendance_sessions_close_consistency",
                "\"closed_at\" IS NULL OR (\"closing_event_id\" IS NOT NULL AND \"closed_at\" >= \"opened_at\")");
        });

        // The duplicate-clock-in guarantee: at most one open session per
        // staff member.
        builder.HasIndex(x => x.StaffUserId)
            .IsUnique()
            .HasDatabaseName("ix_attendance_sessions_open_per_staff")
            .HasFilter("\"closed_at\" IS NULL");

        builder.HasIndex(x => new { x.BusinessId, x.OpenedAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_attendance_sessions_business_opened");

        builder.HasIndex(x => new { x.StaffUserId, x.OpenedAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_attendance_sessions_staff_opened");

        builder.HasOne<Business>()
            .WithMany()
            .HasForeignKey(x => x.BusinessId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.StaffUserId)
            .OnDelete(DeleteBehavior.Restrict);

        // Session ↔ event is a deliberate FK cycle (events.attendance_session_id →
        // sessions; sessions.opening/closing_event_id → events). Both sides are
        // Restrict, so there is no cascade path; the migration scaffolder defers
        // one constraint so the DDL still orders cleanly.
        builder.HasOne<AttendanceEvent>()
            .WithMany()
            .HasForeignKey(x => x.OpeningEventId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<AttendanceEvent>()
            .WithMany()
            .HasForeignKey(x => x.ClosingEventId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<AttendanceLocation>()
            .WithMany()
            .HasForeignKey(x => x.OpeningLocationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<AttendanceLocation>()
            .WithMany()
            .HasForeignKey(x => x.ClosingLocationId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
