using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data.Configurations;

/// <summary>
/// Fluent API configuration for <see cref="RewardEntitlement"/>.
/// </summary>
public class RewardEntitlementConfiguration : IEntityTypeConfiguration<RewardEntitlement>
{
    public void Configure(EntityTypeBuilder<RewardEntitlement> builder)
    {
        builder.ToTable("reward_entitlements");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id");

        builder.Property(e => e.BusinessId).IsRequired().HasColumnName("business_id");
        builder.Property(e => e.ProgramId).IsRequired().HasColumnName("program_id");
        builder.Property(e => e.CardId).IsRequired().HasColumnName("card_id");
        builder.Property(e => e.CustomerId).IsRequired().HasColumnName("customer_id");
        builder.Property(e => e.RewardId).IsRequired().HasColumnName("reward_id");

        builder.Property(e => e.RewardName)
            .IsRequired()
            .HasMaxLength(200)
            .HasColumnName("reward_name");

        builder.Property(e => e.RequiredStamps).IsRequired().HasColumnName("required_stamps");
        builder.Property(e => e.StampsToConsume).IsRequired().HasColumnName("stamps_to_consume");

        builder.Property(e => e.RewardValue)
            .IsRequired()
            .HasColumnType("decimal(18,2)")
            .HasColumnName("reward_value");

        builder.Property(e => e.Status).IsRequired().HasColumnName("status");
        builder.Property(e => e.UnlockedAt).IsRequired().HasColumnName("unlocked_at");
        builder.Property(e => e.ExpiresAt).HasColumnName("expires_at");
        builder.Property(e => e.RedeemedAt).HasColumnName("redeemed_at");
        builder.Property(e => e.RedemptionId).HasColumnName("redemption_id");

        builder.Property(e => e.UnlockKey)
            .IsRequired()
            .HasMaxLength(200)
            .HasColumnName("unlock_key");

        // Invariant: one entitlement per (card, reward, cycle). Two racing
        // qualifying events therefore converge on a single entitlement.
        builder.HasIndex(e => e.UnlockKey)
            .IsUnique()
            .HasDatabaseName("IX_reward_entitlements_UnlockKey");

        builder.HasIndex(e => new { e.CardId, e.Status })
            .HasDatabaseName("IX_reward_entitlements_CardId_Status");

        builder.HasIndex(e => new { e.BusinessId, e.Status })
            .HasDatabaseName("IX_reward_entitlements_BusinessId_Status");

        builder.HasOne(e => e.Program)
            .WithMany()
            .HasForeignKey(e => e.ProgramId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(e => e.Card)
            .WithMany(c => c.RewardEntitlements)
            .HasForeignKey(e => e.CardId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(e => e.Reward)
            .WithMany(r => r.Entitlements)
            .HasForeignKey(e => e.RewardId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(e => e.Redemption)
            .WithMany()
            .HasForeignKey(e => e.RedemptionId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
