using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data.Configurations;

/// <summary>
/// <c>staff_availability</c> — the recurring working week, one row per weekday per
/// member. Dated overrides live in <c>staff_shifts</c> and always win for the date
/// they cover, which is why the two tables are separate: a pattern is edited once
/// and applies forever, an override is written for a date and forgets itself.
/// </summary>
public class StaffAvailabilityConfiguration : IEntityTypeConfiguration<StaffAvailability>
{
    public void Configure(EntityTypeBuilder<StaffAvailability> builder)
    {
        builder.ToTable("staff_availability", t =>
        {
            t.HasCheckConstraint(
                "chk_staff_availability_day_of_week",
                "\"day_of_week\" >= 0 AND \"day_of_week\" <= 6");
            t.HasCheckConstraint(
                "chk_staff_availability_minutes",
                "\"start_minute\" >= 0 AND \"start_minute\" <= 1439 AND " +
                "\"end_minute\" >= 1 AND \"end_minute\" <= 1440 AND " +
                "(\"is_working\" = false OR \"end_minute\" > \"start_minute\") AND " +
                "(\"break_start_minute\" IS NULL OR (\"break_start_minute\" >= \"start_minute\" AND \"break_start_minute\" <= 1439)) AND " +
                "(\"break_end_minute\" IS NULL OR (\"break_end_minute\" <= \"end_minute\" AND \"break_end_minute\" >= 1)) AND " +
                "(\"break_start_minute\" IS NULL OR \"break_end_minute\" IS NULL OR \"break_end_minute\" > \"break_start_minute\")");
        });

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.BusinessId).IsRequired().HasColumnName("business_id");
        builder.Property(x => x.StaffUserId).IsRequired().HasColumnName("staff_user_id");
        builder.Property(x => x.DayOfWeek).IsRequired().HasColumnName("day_of_week");
        builder.Property(x => x.IsWorking).IsRequired().HasColumnName("is_working");
        builder.Property(x => x.StartMinute).IsRequired().HasColumnName("start_minute");
        builder.Property(x => x.EndMinute).IsRequired().HasColumnName("end_minute");
        builder.Property(x => x.BreakStartMinute).HasColumnName("break_start_minute");
        builder.Property(x => x.BreakEndMinute).HasColumnName("break_end_minute");
        builder.Property(x => x.UpdatedAt).IsRequired().HasColumnName("updated_at");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");

        // One pattern row per member per weekday — the guarantee that a saved week
        // can never accumulate duplicates.
        builder.HasIndex(x => new { x.StaffUserId, x.DayOfWeek })
            .IsUnique()
            .HasDatabaseName("ux_staff_availability_staff_day");

        builder.HasIndex(x => new { x.BusinessId, x.StaffUserId })
            .HasDatabaseName("ix_staff_availability_business_staff");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.StaffUserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Business>()
            .WithMany()
            .HasForeignKey(x => x.BusinessId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
