using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data.Configurations;

/// <summary>
/// Fluent API configuration for the <see cref="StampCardRulesChange"/> audit trail.
/// Append-only: the row is the evidence that a rules change was deliberate and
/// attributable, so nothing here is ever updated or deleted.
/// </summary>
public class StampCardRulesChangeConfiguration : IEntityTypeConfiguration<StampCardRulesChange>
{
    public void Configure(EntityTypeBuilder<StampCardRulesChange> builder)
    {
        builder.ToTable("stamp_card_rules_changes", t =>
        {
            t.HasCheckConstraint("ck_stamp_card_rules_changes_affected_non_negative", "\"affected_cards\" >= 0");
            t.HasCheckConstraint("ck_stamp_card_rules_changes_version_positive", "\"rules_version\" >= 1");
        });

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id");

        builder.Property(e => e.StampCardId).IsRequired().HasColumnName("stamp_card_id");
        builder.Property(e => e.BusinessId).IsRequired().HasColumnName("business_id");
        builder.Property(e => e.ChangedByUserId).HasColumnName("changed_by_user_id");
        builder.Property(e => e.ChangedByRole).HasMaxLength(20).HasColumnName("changed_by_role");
        builder.Property(e => e.Field).IsRequired().HasMaxLength(40).HasColumnName("field");
        builder.Property(e => e.OldValue).HasMaxLength(500).HasColumnName("old_value");
        builder.Property(e => e.NewValue).HasMaxLength(500).HasColumnName("new_value");
        builder.Property(e => e.AppliedToExistingCards).IsRequired().HasColumnName("applied_to_existing_cards");
        builder.Property(e => e.AffectedCards).IsRequired().HasColumnName("affected_cards");
        builder.Property(e => e.Reason).HasMaxLength(500).HasColumnName("reason");
        builder.Property(e => e.RulesVersion).IsRequired().HasColumnName("rules_version");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at");

        // Primary read pattern: "the rule-change history of this card, newest first".
        builder.HasIndex(e => new { e.StampCardId, e.CreatedAt })
            .HasDatabaseName("ix_stamp_card_rules_changes_card_created");

        // Tenant-wide audit queries.
        builder.HasIndex(e => new { e.BusinessId, e.CreatedAt })
            .HasDatabaseName("ix_stamp_card_rules_changes_business_created");

        builder.HasOne(e => e.StampCard)
            .WithMany()
            .HasForeignKey(e => e.StampCardId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
