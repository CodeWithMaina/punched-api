using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data.Configurations;

/// <summary>
/// Maps <see cref="AutomationRuleRun"/> onto the <c>automation_rule_runs</c> table.
/// Indexes support tenant-scoped queries, event lookups, and run-history access.
/// </summary>
public class AutomationRuleRunConfiguration : IEntityTypeConfiguration<AutomationRuleRun>
{
    public void Configure(EntityTypeBuilder<AutomationRuleRun> builder)
    {
        builder.ToTable("automation_rule_runs");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");

        builder.Property(x => x.RuleId).HasColumnName("rule_id").IsRequired();
        builder.Property(x => x.RuleVersion).HasColumnName("rule_version").HasDefaultValue(1);
        builder.Property(x => x.SourceEventType).HasColumnName("source_event_type").HasMaxLength(100).IsRequired();
        builder.Property(x => x.SourceAggregateId).HasColumnName("source_aggregate_id");
        builder.Property(x => x.SourceJobId).HasColumnName("source_job_id");
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(20).HasDefaultValue("pending").IsRequired();
        builder.Property(x => x.CreatedNotificationIntentIdsJson)
            .HasColumnName("created_notification_intent_ids_json")
            .HasColumnType("jsonb")
            .HasDefaultValue("[]")
            .IsRequired();
        builder.Property(x => x.CreatedJobIdsJson)
            .HasColumnName("created_job_ids_json")
            .HasColumnType("jsonb")
            .HasDefaultValue("[]")
            .IsRequired();
        builder.Property(x => x.ErrorMessage).HasColumnName("error_message").HasMaxLength(500);
        builder.Property(x => x.ErrorDetailsJson).HasColumnName("error_details_json").HasColumnType("jsonb");
        builder.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(200).IsRequired();
        builder.Property(x => x.BusinessId).HasColumnName("business_id").IsRequired();
        builder.Property(x => x.StartedAt).HasColumnName("started_at").HasDefaultValueSql("now()");
        builder.Property(x => x.CompletedAt).HasColumnName("completed_at");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");

        builder.HasOne<AutomationRule>()
            .WithMany()
            .HasForeignKey(x => x.RuleId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Business>()
            .WithMany()
            .HasForeignKey(x => x.BusinessId)
            .OnDelete(DeleteBehavior.Cascade);

        // Tenant-scoped queries.
        builder.HasIndex(x => new { x.BusinessId, x.CreatedAt })
            .HasDatabaseName("ix_automation_rule_runs_business_created");
        builder.HasIndex(x => new { x.BusinessId, x.Status, x.CreatedAt })
            .HasDatabaseName("ix_automation_rule_runs_business_status");
        // Event lookups (by aggregate).
        builder.HasIndex(x => new { x.SourceEventType, x.SourceAggregateId })
            .HasDatabaseName("ix_automation_rule_runs_event_aggregate");
        // Idempotency dedupe.
        builder.HasIndex(x => x.IdempotencyKey)
            .HasDatabaseName("ux_automation_rule_runs_idempotency_key")
            .IsUnique();
        // Rule version + run history.
        builder.HasIndex(x => new { x.RuleId, x.RuleVersion, x.CreatedAt })
            .HasDatabaseName("ix_automation_rule_runs_rule_version");
    }
}