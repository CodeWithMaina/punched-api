using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data.Configurations;

/// <summary>
/// Fluent API configuration for LoyaltyCard entity.
/// Unique constraint: one card per customer per business.
/// </summary>
public class LoyaltyCardConfiguration : IEntityTypeConfiguration<LoyaltyCard>
{
    public void Configure(EntityTypeBuilder<LoyaltyCard> builder)
    {
        builder.ToTable("loyalty_cards");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id");

        builder.Property(e => e.CustomerId)
            .IsRequired()
            .HasColumnName("customer_id");

        builder.Property(e => e.BusinessId)
            .IsRequired()
            .HasColumnName("business_id");

        builder.Property(e => e.ProgramId)
            .IsRequired()
            .HasColumnName("program_id");

        builder.Property(e => e.TotalStamps)
            .HasDefaultValue(0)
            .HasColumnName("total_stamps");

        builder.Property(e => e.LifetimeStamps)
            .HasDefaultValue(0)
            .HasColumnName("lifetime_stamps");

        builder.Property(e => e.TotalRedemptions)
            .HasDefaultValue(0)
            .HasColumnName("total_redemptions");

        builder.Property(e => e.LastStampAt)
            .HasColumnName("last_stamp_at");

        builder.Property(e => e.EnrolledAt)
            .HasColumnName("enrolled_at");

        builder.Property(e => e.CreatedAt)
            .HasColumnName("created_at");

        // Check constraints (using ToTable API)
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("chk_total_stamps_gte_zero", "\"total_stamps\" >= 0");
            t.HasCheckConstraint("chk_lifetime_stamps_gte_zero", "\"lifetime_stamps\" >= 0");
            t.HasCheckConstraint("chk_lifetime_gte_total", "\"lifetime_stamps\" >= \"total_stamps\"");
            // The rules snapshot can never hold a nonsensical goal (0 = legacy
            // "not snapshotted", which readers treat as "fall back to program").
            t.HasCheckConstraint(
                "chk_required_stamps_range",
                "\"required_stamps\" >= 0 AND \"required_stamps\" <= 100");
            t.HasCheckConstraint("chk_rules_version_non_negative", "\"rules_version\" >= 0");
        });

        // ── Rules snapshot (see LoyaltyCardSnapshot / CardRulesPolicy) ──────
        // NOTE: RewardExpiresAt is deliberately NOT mapped — its production column
        // has always been the conventionally-named "RewardExpiresAt", and renaming
        // it would break existing data.
        builder.Property(e => e.StampCardId).HasColumnName("stamp_card_id");
        builder.Property(e => e.RequiredStamps)
            .HasColumnName("required_stamps")
            .HasDefaultValue(0);
        builder.Property(e => e.RulesVersion)
            .HasColumnName("rules_version")
            .HasDefaultValue(0);

        // Unique: one card per customer per business
        builder.HasIndex(e => new { e.CustomerId, e.BusinessId }).IsUnique();
        builder.HasIndex(e => new { e.BusinessId, e.LastStampAt });
        builder.HasIndex(e => new { e.BusinessId, e.EnrolledAt });
        builder.HasIndex(e => new { e.BusinessId, e.ProgramId });
        builder.HasIndex(e => e.CustomerId);
        // Audit/ops read pattern: progress rows bound to a stamp card
        // ("which customers are affected by a rules change?").
        builder.HasIndex(e => e.StampCardId);

        // Binding is presentation-agnostic; SET NULL keeps progress intact if a
        // card template is ever hard-deleted — the RequiredStamps snapshot still
        // freezes the customer's rule, so no loyalty history is lost.
        builder.HasOne(e => e.StampCard)
            .WithMany(c => c.LoyaltyCards)
            .HasForeignKey(e => e.StampCardId)
            .OnDelete(DeleteBehavior.SetNull);

        // Relationships
        builder.HasOne(e => e.Customer)
            .WithMany(u => u.LoyaltyCards)
            .HasForeignKey(e => e.CustomerId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(e => e.Business)
            .WithMany(b => b.LoyaltyCards)
            .HasForeignKey(e => e.BusinessId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(e => e.Program)
            .WithMany(p => p.LoyaltyCards)
            .HasForeignKey(e => e.ProgramId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
