using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data.Configurations;

public class AnalyticsBackfillRunConfiguration : IEntityTypeConfiguration<AnalyticsBackfillRun>
{
    public void Configure(EntityTypeBuilder<AnalyticsBackfillRun> builder)
    {
        builder.ToTable("analytics_backfill_runs");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.RunGroupId).HasColumnName("run_group_id");
        builder.Property(x => x.BusinessId).HasColumnName("business_id");
        builder.Property(x => x.FromDate).HasColumnName("from_date");
        builder.Property(x => x.ToDate).HasColumnName("to_date");
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(20);
        builder.Property(x => x.LastCompletedDate).HasColumnName("last_completed_date");
        builder.Property(x => x.TotalDays).HasColumnName("total_days");
        builder.Property(x => x.CompletedDays).HasColumnName("completed_days");
        builder.Property(x => x.StartedAt).HasColumnName("started_at");
        builder.Property(x => x.LastProgressAt).HasColumnName("last_progress_at");
        builder.Property(x => x.CompletedAt).HasColumnName("completed_at");
        builder.Property(x => x.Error).HasColumnName("error").HasMaxLength(2000);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");

        builder.ToTable(t =>
        {
            t.HasCheckConstraint(
                "chk_backfill_status_valid",
                "\"status\" IN ('Pending','Running','Completed','Failed','Cancelled')");
            t.HasCheckConstraint("chk_backfill_range_valid", "\"to_date\" >= \"from_date\"");
        });

        // One row per (run group, business); coordinator uses a fixed zero GUID
        // for BusinessId=NULL mapping via coalesce — enforced in app code with a
        // unique index on (run_group_id, business_id) plus a partial unique index
        // for the coordinator row.
        builder.HasIndex(x => new { x.RunGroupId, x.BusinessId })
            .HasDatabaseName("ix_backfill_runs_group_business");

        builder.HasIndex(x => x.Status).HasDatabaseName("ix_backfill_runs_status");
        builder.HasIndex(x => x.BusinessId).HasDatabaseName("ix_backfill_runs_business");
    }
}
