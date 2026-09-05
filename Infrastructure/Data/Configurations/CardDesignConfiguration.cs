using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data.Configurations;

/// <summary>
/// Fluent API configuration for <see cref="CardDesign"/>.
/// Designs are reusable per business — many stamp cards can share one design.
/// </summary>
public class CardDesignConfiguration : IEntityTypeConfiguration<CardDesign>
{
    public void Configure(EntityTypeBuilder<CardDesign> builder)
    {
        builder.ToTable("card_designs");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id");

        builder.Property(e => e.BusinessId).IsRequired().HasColumnName("business_id");

        builder.Property(e => e.Name)
            .IsRequired()
            .HasMaxLength(100)
            .HasColumnName("name")
            .HasDefaultValue("Card Design");

        builder.Property(e => e.HtmlTemplate).IsRequired().HasMaxLength(50000).HasColumnName("html_template");
        builder.Property(e => e.IsActive).IsRequired().HasColumnName("is_active").HasDefaultValue(true);
        builder.Property(e => e.CreatedAt).HasColumnName("created_at");

        builder.HasIndex(e => e.BusinessId);

        builder.HasOne(e => e.Business)
            .WithMany()
            .HasForeignKey(e => e.BusinessId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
