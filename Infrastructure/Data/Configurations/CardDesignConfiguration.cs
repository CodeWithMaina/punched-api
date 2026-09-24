using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data.Configurations;

/// <summary>
/// Fluent API configuration for <see cref="CardDesign"/>.
/// Designs are reusable per business (many loyalty programs / stamp cards can
/// share one design) plus exactly one platform-wide default
/// (<c>business_id IS NULL AND is_default = true</c>).
/// </summary>
public class CardDesignConfiguration : IEntityTypeConfiguration<CardDesign>
{
    public void Configure(EntityTypeBuilder<CardDesign> builder)
    {
        builder.ToTable("card_designs", t =>
        {
            // A design is either business-owned or the single platform default.
            t.HasCheckConstraint(
                "ck_card_designs_default_is_system",
                "\"is_default\" = FALSE OR \"business_id\" IS NULL");
        });

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id");

        // Nullable: null ⇒ platform/system design.
        builder.Property(e => e.BusinessId).IsRequired(false).HasColumnName("business_id");

        builder.Property(e => e.Name)
            .IsRequired()
            .HasMaxLength(100)
            .HasColumnName("name")
            .HasDefaultValue("Card Design");

        builder.Property(e => e.HtmlTemplate).IsRequired().HasMaxLength(50000).HasColumnName("html_template");
        builder.Property(e => e.IsActive).IsRequired().HasColumnName("is_active").HasDefaultValue(true);
        builder.Property(e => e.IsDefault).IsRequired().HasColumnName("is_default").HasDefaultValue(false);
        builder.Property(e => e.CreatedAt).HasColumnName("created_at");

        // Structured presentation configuration + append-only versioning metadata.
        builder.Property(e => e.ConfigJson).HasColumnName("config_json");
        builder.Property(e => e.CurrentVersion)
            .HasColumnName("current_version")
            .HasDefaultValue(0);
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at");

        builder.HasIndex(e => e.BusinessId);

        // Exactly one platform default (partial unique index; ignored by SQLite
        // providers that cannot express filters — the service enforces it too).
        builder.HasIndex(e => e.IsDefault)
            .HasDatabaseName("ux_card_designs_single_default")
            .IsUnique()
            .HasFilter("\"is_default\" = TRUE");

        builder.HasOne(e => e.Business)
            .WithMany()
            .HasForeignKey(e => e.BusinessId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
