using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data.Configurations;

/// <summary>
/// Fluent API configuration for <see cref="NotificationPreference"/>.
/// Sparse override table: rows exist only for deviations from the code default,
/// so a user with no preferences has zero rows.
/// </summary>
public class NotificationPreferenceConfiguration : IEntityTypeConfiguration<NotificationPreference>
{
    public void Configure(EntityTypeBuilder<NotificationPreference> builder)
    {
        builder.ToTable("notification_preferences", t =>
        {
            // Three row shapes: global user, per-business user, business kill-switch.
            t.HasCheckConstraint(
                "ck_notification_preferences_scope",
                "\"user_id\" IS NOT NULL OR \"business_id\" IS NOT NULL");
        });

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id)
            .HasColumnName("id")
            .HasDefaultValueSql("gen_random_uuid()")
            .ValueGeneratedOnAdd();


        builder.Property(x => x.UserId).IsRequired(false).HasColumnName("user_id");
        builder.Property(x => x.BusinessId).IsRequired(false).HasColumnName("business_id");

        builder.Property(x => x.Category).IsRequired().HasMaxLength(30).HasColumnName("category");
        builder.Property(x => x.Channel).IsRequired().HasMaxLength(20).HasColumnName("channel");
        builder.Property(x => x.Enabled).IsRequired().HasColumnName("enabled");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("now()");

        // Configuration rows follow their owner out of existence (unlike the
        // notification_inbox audit artefact, which restricts deletion).
        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Business>()
            .WithMany()
            .HasForeignKey(x => x.BusinessId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Cascade);

        // Postgres treats NULLs as distinct in a composite unique index, so the
        // three shapes each need their own PARTIAL unique index.
        builder.HasIndex(x => new { x.UserId, x.Category, x.Channel })
            .HasDatabaseName("ux_notification_prefs_user_global")
            .IsUnique()
            .HasFilter("\"business_id\" IS NULL AND \"user_id\" IS NOT NULL");

        builder.HasIndex(x => new { x.BusinessId, x.Category, x.Channel })
            .HasDatabaseName("ux_notification_prefs_business")
            .IsUnique()
            .HasFilter("\"user_id\" IS NULL");

        builder.HasIndex(x => new { x.UserId, x.BusinessId, x.Category, x.Channel })
            .HasDatabaseName("ux_notification_prefs_user_business")
            .IsUnique()
            .HasFilter("\"business_id\" IS NOT NULL AND \"user_id\" IS NOT NULL");

        // The resolver's hot query: all overrides for this user in this business.
        builder.HasIndex(x => new { x.UserId, x.BusinessId, x.Category })
            .HasDatabaseName("ix_notification_prefs_lookup");
    }
}
