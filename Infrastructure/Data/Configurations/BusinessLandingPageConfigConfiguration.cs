using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data.Configurations;

/// <summary>
/// Fluent API configuration for <see cref="BusinessLandingPageConfig"/>.
/// One row per business (business_id is PK + FK, cascade on business delete).
/// Hero background is a real FK to media with SET NULL so deleting the image
/// falls back to the default storefront photograph.
/// </summary>
public class BusinessLandingPageConfigConfiguration : IEntityTypeConfiguration<BusinessLandingPageConfig>
{
    public void Configure(EntityTypeBuilder<BusinessLandingPageConfig> builder)
    {
        builder.ToTable("business_landing_page_configs");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id");

        builder.Property(e => e.BusinessId).IsRequired().HasColumnName("business_id");
        builder.HasIndex(e => e.BusinessId).IsUnique()
            .HasDatabaseName("ux_business_landing_page_configs_business");

        builder.Property(e => e.Version).IsRequired().HasColumnName("version").HasDefaultValue(0);
        builder.Property(e => e.UpdatedAt).IsRequired().HasColumnName("updated_at");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at");

        builder.Property(e => e.HeroBackgroundMediaId).HasColumnName("hero_background_media_id");
        builder.HasIndex(e => e.HeroBackgroundMediaId);

        builder.Property(e => e.ConfigJson).IsRequired().HasColumnName("config_json");

        builder.HasOne(e => e.Business)
            .WithMany()
            .HasForeignKey(e => e.BusinessId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(e => e.HeroBackgroundMedia)
            .WithMany()
            .HasForeignKey(e => e.HeroBackgroundMediaId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
