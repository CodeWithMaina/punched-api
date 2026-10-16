using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data.Configurations;

/// <summary>
/// Maps <see cref="NotificationSuppression"/> onto the table created by
/// <c>20261020000001_AddNotificationSuppressionTable</c> — column-for-column,
/// index-for-index (additive migrations stay authoritative).
/// </summary>
public class NotificationSuppressionConfiguration : IEntityTypeConfiguration<NotificationSuppression>
{
    public void Configure(EntityTypeBuilder<NotificationSuppression> builder)
    {
        builder.ToTable("notification_suppressions");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.BusinessId).HasColumnName("business_id");
        builder.Property(x => x.UserId).HasColumnName("user_id");
        builder.Property(x => x.Channel).HasColumnName("channel").HasMaxLength(20);
        builder.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(64);
        builder.Property(x => x.Source).HasColumnName("source").HasMaxLength(64);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.ExpiresAt).HasColumnName("expires_at");

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Business>()
            .WithMany()
            .HasForeignKey(x => x.BusinessId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.BusinessId, x.UserId, x.Channel })
            .HasDatabaseName("ix_notification_suppressions_business_user_channel");

        builder.HasIndex(x => new { x.UserId, x.Channel })
            .HasDatabaseName("ix_notification_suppressions_user_channel");

        builder.HasIndex(x => new { x.BusinessId, x.UserId, x.Channel })
            .HasDatabaseName("ux_notification_suppressions_one_per_user_channel")
            .IsUnique()
            .HasFilter("\"user_id\" IS NOT NULL");
    }
}