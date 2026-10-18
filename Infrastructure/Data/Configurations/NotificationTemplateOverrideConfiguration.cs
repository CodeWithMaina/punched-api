using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data.Configurations;

/// <summary>
/// Maps <see cref="NotificationTemplateOverride"/> onto the
/// <c>notification_template_overrides</c> table.
/// Unique (business_id, type, channel) per business.
/// </summary>
public class NotificationTemplateOverrideConfiguration : IEntityTypeConfiguration<NotificationTemplateOverride>
{
    public void Configure(EntityTypeBuilder<NotificationTemplateOverride> builder)
    {
        builder.ToTable("notification_template_overrides");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");

        builder.Property(x => x.BusinessId).HasColumnName("business_id").IsRequired();
        builder.Property(x => x.Type).HasColumnName("type").HasMaxLength(100).IsRequired();
        builder.Property(x => x.Channel).HasColumnName("channel").HasMaxLength(20).IsRequired();
        builder.Property(x => x.SubjectTemplate).HasColumnName("subject_template").HasMaxLength(200);
        builder.Property(x => x.BodyTemplateHtml).HasColumnName("body_template_html").IsRequired();
        builder.Property(x => x.BodyTemplateText).HasColumnName("body_template_text").IsRequired();
        builder.Property(x => x.Version).HasColumnName("version").HasDefaultValue(1);
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        builder.Property(x => x.UpdatedBy).HasColumnName("updated_by").HasMaxLength(200).IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");

        builder.HasOne(x => x.Business)
            .WithMany()
            .HasForeignKey(x => x.BusinessId)
            .OnDelete(DeleteBehavior.Cascade);

        // Unique per business + type + channel.
        builder.HasIndex(x => new { x.BusinessId, x.Type, x.Channel })
            .HasDatabaseName("ux_notification_template_overrides_business_type_channel")
            .IsUnique();
        // Business-scoped template lookups.
        builder.HasIndex(x => new { x.BusinessId, x.Type })
            .HasDatabaseName("ix_notification_template_overrides_business_type");
    }
}