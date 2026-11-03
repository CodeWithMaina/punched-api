using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data.Configurations;

public class StaffShiftConfiguration : IEntityTypeConfiguration<StaffShift>
{
    public void Configure(EntityTypeBuilder<StaffShift> builder)
    {
        builder.ToTable("staff_shifts");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.StaffUserId).HasColumnName("staff_user_id");
        builder.Property(x => x.BusinessId).HasColumnName("business_id");
        builder.Property(x => x.Date).HasColumnName("date");
        builder.Property(x => x.StartHour).HasColumnName("start_hour");
        builder.Property(x => x.EndHour).HasColumnName("end_hour");
        builder.Property(x => x.StartMinute).HasColumnName("start_minute");
        builder.Property(x => x.EndMinute).HasColumnName("end_minute");
        builder.Property(x => x.BreakStartMinute).HasColumnName("break_start_minute");
        builder.Property(x => x.BreakEndMinute).HasColumnName("break_end_minute");
        builder.Property(x => x.Note).HasMaxLength(200).HasColumnName("note");
        builder.Property(x => x.IsWorking).HasColumnName("is_working");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("chk_staff_shift_start_hour", "\"start_hour\" >= 0 AND \"start_hour\" <= 23");
            t.HasCheckConstraint("chk_staff_shift_end_hour", "\"end_hour\" >= 0 AND \"end_hour\" <= 23");
            // Minute-precise refinement is optional; when present it must be a real
            // time of day, and the break must sit inside the shift. Existing rows
            // keep NULL here and are read through their hour columns.
            t.HasCheckConstraint(
                "chk_staff_shift_minutes",
                "(\"start_minute\" IS NULL OR (\"start_minute\" >= 0 AND \"start_minute\" <= 1439)) AND " +
                "(\"end_minute\" IS NULL OR (\"end_minute\" >= 1 AND \"end_minute\" <= 1440)) AND " +
                "(\"break_start_minute\" IS NULL OR (\"break_start_minute\" >= 0 AND \"break_start_minute\" <= 1439)) AND " +
                "(\"break_end_minute\" IS NULL OR (\"break_end_minute\" >= 1 AND \"break_end_minute\" <= 1440))");
        });

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.StaffUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Business>()
            .WithMany()
            .HasForeignKey(x => x.BusinessId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.StaffUserId, x.Date });
        builder.HasIndex(x => new { x.BusinessId, x.Date });
    }
}
