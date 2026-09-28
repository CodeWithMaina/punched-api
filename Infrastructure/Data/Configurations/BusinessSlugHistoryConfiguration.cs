using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data.Configurations;

/// <summary>
/// Fluent API configuration for the superseded-slug redirect table.
/// </summary>
public class BusinessSlugHistoryConfiguration : IEntityTypeConfiguration<BusinessSlugHistory>
{
    public void Configure(EntityTypeBuilder<BusinessSlugHistory> builder)
    {
        builder.ToTable("business_slug_history");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id");

        builder.Property(e => e.Slug)
            .IsRequired()
            .HasMaxLength(63)
            .HasColumnName("slug");

        builder.Property(e => e.BusinessId)
            .HasColumnName("business_id");

        builder.Property(e => e.CreatedAt)
            .HasColumnName("created_at");

        // Former slugs stay claimed while their business is alive: one history
        // row per slug, and no two businesses can ever hold the same address
        // across the live + history tables combined (checked app-side).
        builder.HasIndex(e => e.Slug).IsUnique();
        builder.HasIndex(e => e.BusinessId);

        builder.HasOne(e => e.Business)
            .WithMany()
            .HasForeignKey(e => e.BusinessId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}