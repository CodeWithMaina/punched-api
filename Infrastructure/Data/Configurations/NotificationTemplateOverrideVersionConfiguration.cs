using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data.Configurations;

/// <summary>
/// Maps <see cref="NotificationTemplateOverrideVersion"/> onto the
/// <c>notification_template_override_versions</c> table.
/// </summary>
public class NotificationTemplateOverrideVersionConfiguration : IEntityTypeConfiguration<NotificationTemplateOverrideVersion>
{
    public void Configure(EntityTypeBuilder<NotificationTemplateOverrideVersion> builder)
    {
        builder.ToTable("notification_template_override_versions");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");

        builder.Property(x => x.OverrideId).HasColumnName("override_id").IsRequired();
        builder.Property(x => x.BusinessId).HasColumnName("business_id").IsRequired();
        builder.Property(x => x.Type).HasColumnName("type").HasMaxLength(100).IsRequired();
        builder.Property(x => x.Channel).HasColumnName("channel").HasMaxLength(20).IsRequired();
        builder.Property(x => x.SubjectTemplate).HasColumnName("subject_template").HasMaxLength(200);
        builder.Property(x => x.BodyTemplateHtml).HasColumnName("body_template_html").IsRequired();
        builder.Property(x => x.BodyTemplateText).HasColumnName("body_template_text").IsRequired();
        builder.Property(x => x.Version).HasColumnName("version").IsRequired();
        builder.Property(x => x.UpdatedBy).HasColumnName("updated_by").HasMaxLength(200).IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");

        builder.HasOne(x => x.Override)
            .WithMany()
            .HasForeignKey(x => x.OverrideId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.OverrideId, x.Version })
            .HasDatabaseName("ux_notification_template_override_versions_override_version")
            .IsUnique();
        builder.HasIndex(x => new { x.BusinessId, x.Type, x.Channel })
            .HasDatabaseName("ix_notification_template_override_versions_business_type_channel");
        builder.HasIndex(x => x.OverrideId)
            .HasDatabaseName("ix_notification_template_override_versions_override_id");
    }
}
