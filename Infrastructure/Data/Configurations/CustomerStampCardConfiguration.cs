using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data.Configurations;

/// <summary>Fluent configuration for CustomerStampCard (customer_stamp_cards).</summary>
public class CustomerStampCardConfiguration : IEntityTypeConfiguration<CustomerStampCard>
{
    public void Configure(EntityTypeBuilder<CustomerStampCard> builder)
    {
        builder.ToTable("customer_stamp_cards");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id");
        builder.Property(e => e.CustomerId).IsRequired().HasColumnName("customer_id");
        builder.Property(e => e.StampCardId).IsRequired().HasColumnName("stamp_card_id");
        builder.Property(e => e.Status).IsRequired().HasColumnName("status").HasDefaultValue(CustomerStampCardStatus.Active);
        builder.Property(e => e.JoinedAt).IsRequired().HasColumnName("joined_at");
        builder.Property(e => e.LeftAt).HasColumnName("left_at");
        builder.Property(e => e.UpdatedAt).IsRequired().HasColumnName("updated_at");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at");

        builder.HasIndex(e => new { e.CustomerId, e.StampCardId }).IsUnique();
        builder.HasIndex(e => e.StampCardId);
        builder.HasIndex(e => new { e.CustomerId, e.Status });

        builder.HasOne(e => e.Customer).WithMany().HasForeignKey(e => e.CustomerId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(e => e.StampCard).WithMany().HasForeignKey(e => e.StampCardId).OnDelete(DeleteBehavior.Restrict);
    }
}
