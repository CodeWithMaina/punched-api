using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data.Configurations;

public sealed class ServiceMediaConfiguration : IEntityTypeConfiguration<ServiceMedia>
{
    public void Configure(EntityTypeBuilder<ServiceMedia> b)
    {
        b.ToTable("service_media", t => t.HasCheckConstraint("ck_service_media_order", "\"sort_order\" >= 0"));
        b.HasKey(x => new { x.ServiceCatalogItemId, x.MediaId });
        b.Property(x => x.ServiceCatalogItemId).HasColumnName("service_id");
        b.Property(x => x.MediaId).HasColumnName("media_id");
        b.Property(x => x.Role).HasColumnName("role").HasMaxLength(20);
        b.Property(x => x.SortOrder).HasColumnName("sort_order");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.HasIndex(x => new { x.ServiceCatalogItemId, x.SortOrder, x.CreatedAt }).HasDatabaseName("ix_service_media_order");
        b.HasOne(x => x.ServiceCatalogItem).WithMany().HasForeignKey(x => x.ServiceCatalogItemId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Media).WithMany().HasForeignKey(x => x.MediaId).OnDelete(DeleteBehavior.Restrict);
    }
}
