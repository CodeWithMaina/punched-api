using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data.Configurations;

/// <summary>
/// Maps the append-only <see cref="SubscriptionAuditLog"/> entity to the
/// snake_case <c>subscription_audit_logs</c> table. All FKs use restrictive
/// delete behaviour so audit records can never become orphaned.
/// </summary>
public class SubscriptionAuditLogConfiguration : IEntityTypeConfiguration<SubscriptionAuditLog>
{
    public void Configure(EntityTypeBuilder<SubscriptionAuditLog> builder)
    {
        builder.ToTable("subscription_audit_logs");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.Action).HasColumnName("action").HasMaxLength(64);
        builder.Property(x => x.ActorUserId).HasColumnName("actor_user_id");
        builder.Property(x => x.TargetBusinessId).HasColumnName("target_business_id");
        builder.Property(x => x.TargetPlanId).HasColumnName("target_plan_id");
        builder.Property(x => x.PayloadJson).HasColumnName("payload_json");
        builder.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(1000);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");

        builder.HasOne(x => x.ActorUser)
            .WithMany()
            .HasForeignKey(x => x.ActorUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.TargetBusiness)
            .WithMany()
            .HasForeignKey(x => x.TargetBusinessId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.TargetPlan)
            .WithMany()
            .HasForeignKey(x => x.TargetPlanId)
            .OnDelete(DeleteBehavior.Restrict);

        // Indexes for the admin audit feed.
        builder.HasIndex(x => x.TargetPlanId)
            .HasDatabaseName("ix_subscription_audit_logs_target_plan_id");
        builder.HasIndex(x => new { x.TargetBusinessId, x.CreatedAt })
            .HasDatabaseName("ix_subscription_audit_logs_target_business_created");
        builder.HasIndex(x => x.ActorUserId)
            .HasDatabaseName("ix_subscription_audit_logs_actor_user_id");
        builder.HasIndex(x => x.CreatedAt)
            .HasDatabaseName("ix_subscription_audit_logs_created_at");
    }
}