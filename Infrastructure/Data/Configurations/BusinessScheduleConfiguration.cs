using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data.Configurations;

/// <summary>Weekly business operating hours (one row per weekday).</summary>
public class BusinessOperatingHourConfiguration : IEntityTypeConfiguration<BusinessOperatingHour>
{
    public void Configure(EntityTypeBuilder<BusinessOperatingHour> builder)
    {
        builder.ToTable("business_operating_hours", t =>
        {
            t.HasCheckConstraint("chk_business_operating_hours_day",
                "\"day_of_week\" >= 0 AND \"day_of_week\" <= 6");
        });

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.BusinessId).IsRequired().HasColumnName("business_id");
        builder.Property(x => x.DayOfWeek).IsRequired().HasColumnName("day_of_week");
        builder.Property(x => x.IsClosed).IsRequired().HasColumnName("is_closed");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");

        builder.HasIndex(x => new { x.BusinessId, x.DayOfWeek })
            .IsUnique()
            .HasDatabaseName("ux_business_operating_hours_business_day");

        builder.HasMany(x => x.Periods)
            .WithOne(p => p.OperatingHour)
            .HasForeignKey(p => p.BusinessOperatingHourId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Business>()
            .WithMany()
            .HasForeignKey(x => x.BusinessId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class BusinessOperatingPeriodConfiguration : IEntityTypeConfiguration<BusinessOperatingPeriod>
{
    public void Configure(EntityTypeBuilder<BusinessOperatingPeriod> builder)
    {
        builder.ToTable("business_operating_periods", t =>
        {
            t.HasCheckConstraint("chk_business_operating_periods_minutes",
                "\"start_minute\" >= 0 AND \"start_minute\" <= 1439 AND " +
                "\"end_minute\" >= 1 AND \"end_minute\" <= 1440 AND \"end_minute\" > \"start_minute\"");
        });

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.BusinessOperatingHourId).IsRequired().HasColumnName("business_operating_hour_id");
        builder.Property(x => x.StartMinute).IsRequired().HasColumnName("start_minute");
        builder.Property(x => x.EndMinute).IsRequired().HasColumnName("end_minute");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");

        builder.HasIndex(x => x.BusinessOperatingHourId)
            .HasDatabaseName("ix_business_operating_periods_hour");
    }
}

/// <summary>Dated overrides (holidays, closures, special hours) of the weekly schedule.</summary>
public class BusinessScheduleExceptionConfiguration : IEntityTypeConfiguration<BusinessScheduleException>
{
    public void Configure(EntityTypeBuilder<BusinessScheduleException> builder)
    {
        builder.ToTable("business_schedule_exceptions");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.BusinessId).IsRequired().HasColumnName("business_id");
        builder.Property(x => x.Date).IsRequired().HasColumnName("date");
        builder.Property(x => x.IsClosed).IsRequired().HasColumnName("is_closed");
        builder.Property(x => x.Reason).HasMaxLength(200).HasColumnName("reason");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");

        builder.HasIndex(x => new { x.BusinessId, x.Date })
            .IsUnique()
            .HasDatabaseName("ux_business_schedule_exceptions_business_date");

        builder.HasMany(x => x.Periods)
            .WithOne(p => p.Exception)
            .HasForeignKey(p => p.BusinessScheduleExceptionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Business>()
            .WithMany()
            .HasForeignKey(x => x.BusinessId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class BusinessScheduleExceptionPeriodConfiguration : IEntityTypeConfiguration<BusinessScheduleExceptionPeriod>
{
    public void Configure(EntityTypeBuilder<BusinessScheduleExceptionPeriod> builder)
    {
        builder.ToTable("business_schedule_exception_periods", t =>
        {
            t.HasCheckConstraint("chk_business_schedule_exception_periods_minutes",
                "\"start_minute\" >= 0 AND \"start_minute\" <= 1439 AND " +
                "\"end_minute\" >= 1 AND \"end_minute\" <= 1440 AND \"end_minute\" > \"start_minute\"");
        });

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.BusinessScheduleExceptionId).IsRequired().HasColumnName("business_schedule_exception_id");
        builder.Property(x => x.StartMinute).IsRequired().HasColumnName("start_minute");
        builder.Property(x => x.EndMinute).IsRequired().HasColumnName("end_minute");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");

        builder.HasIndex(x => x.BusinessScheduleExceptionId)
            .HasDatabaseName("ix_business_schedule_exception_periods_exception");
    }
}
