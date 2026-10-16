using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data.Configurations;

/// <summary>
/// Phase 2 scheduling core — column-for-column, index-for-index against the
/// hand-written additive migration
/// <c>20261101000000_AddSchedulingAndDomainEvents</c> (additive migrations stay
/// authoritative).
/// </summary>
public class ScheduledJobConfiguration : IEntityTypeConfiguration<ScheduledJob>
{
    public void Configure(EntityTypeBuilder<ScheduledJob> builder)
    {
        builder.ToTable("scheduled_jobs");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.JobType).HasColumnName("job_type").HasMaxLength(60);
        builder.Property(x => x.DedupeKey).HasColumnName("dedupe_key").HasMaxLength(200);
        builder.Property(x => x.BusinessId).HasColumnName("business_id");
        builder.Property(x => x.AggregateType).HasColumnName("aggregate_type").HasMaxLength(40);
        builder.Property(x => x.AggregateId).HasColumnName("aggregate_id");
        builder.Property(x => x.PayloadJson).HasColumnName("payload_json").HasColumnType("jsonb").HasDefaultValue("{}");
        builder.Property(x => x.ScheduledForUtc).HasColumnName("scheduled_for_utc");
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(20).HasDefaultValue("pending");
        builder.Property(x => x.Attempts).HasColumnName("attempts").HasDefaultValue(0);
        builder.Property(x => x.LockedAt).HasColumnName("locked_at");
        builder.Property(x => x.CompletedAt).HasColumnName("completed_at");
        builder.Property(x => x.CancelledAt).HasColumnName("cancelled_at");
        builder.Property(x => x.Error).HasColumnName("error").HasMaxLength(500);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");

        // One row per logical job: reschedule rewrites due times instead of inserting.
        builder.HasIndex(x => x.DedupeKey)
            .IsUnique()
            .HasDatabaseName("ux_scheduled_jobs_dedupe_key");

        builder.HasIndex(x => new { x.Status, x.ScheduledForUtc })
            .HasDatabaseName("ix_scheduled_jobs_status_due");

        builder.HasIndex(x => new { x.AggregateType, x.AggregateId })
            .HasDatabaseName("ix_scheduled_jobs_aggregate");
    }
}

public class DomainEventConfiguration : IEntityTypeConfiguration<DomainEvent>
{
    public void Configure(EntityTypeBuilder<DomainEvent> builder)
    {
        builder.ToTable("domain_events");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.EventType).HasColumnName("event_type").HasMaxLength(100);
        builder.Property(x => x.AggregateType).HasColumnName("aggregate_type").HasMaxLength(40);
        builder.Property(x => x.AggregateId).HasColumnName("aggregate_id");
        builder.Property(x => x.BusinessId).HasColumnName("business_id");
        builder.Property(x => x.PayloadJson).HasColumnName("payload_json").HasColumnType("jsonb").HasDefaultValue("{}");
        builder.Property(x => x.OccurredAtUtc).HasColumnName("occurred_at_utc");
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(20).HasDefaultValue("pending");
        builder.Property(x => x.Attempts).HasColumnName("attempts").HasDefaultValue(0);
        builder.Property(x => x.HandledByJson).HasColumnName("handled_by_json").HasColumnType("jsonb").HasDefaultValue("[]");
        builder.Property(x => x.LockedAt).HasColumnName("locked_at");
        builder.Property(x => x.CompletedAt).HasColumnName("completed_at");
        builder.Property(x => x.Error).HasColumnName("error").HasMaxLength(500);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");

        builder.HasIndex(x => new { x.Status, x.OccurredAtUtc })
            .HasDatabaseName("ix_domain_events_status_occurred");

        builder.HasIndex(x => new { x.AggregateType, x.AggregateId })
            .HasDatabaseName("ix_domain_events_aggregate");

        builder.HasIndex(x => x.EventType)
            .HasDatabaseName("ix_domain_events_event_type");
    }
}

public class BusinessReminderPolicyConfiguration : IEntityTypeConfiguration<BusinessReminderPolicy>
{
    public void Configure(EntityTypeBuilder<BusinessReminderPolicy> builder)
    {
        builder.ToTable("business_reminder_policies");

        builder.HasKey(x => x.BusinessId);
        builder.Property(x => x.BusinessId).HasColumnName("business_id");
        builder.Property(x => x.Enabled).HasColumnName("enabled").HasDefaultValue(true);
        builder.Property(x => x.OffsetMinutesJson).HasColumnName("offset_minutes_json").HasColumnType("jsonb").HasDefaultValue("[1440,60]");
        builder.Property(x => x.ChannelsJson).HasColumnName("channels_json").HasColumnType("jsonb").HasDefaultValue("[\"in_app\",\"email\",\"push\"]");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");

        builder.HasOne<Business>()
            .WithMany()
            .HasForeignKey(x => x.BusinessId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}