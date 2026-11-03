using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data.Configurations;

public sealed class BusinessMediaConfiguration : IEntityTypeConfiguration<BusinessMedia>
{
    public void Configure(EntityTypeBuilder<BusinessMedia> b)
    {
        b.ToTable("business_media", t => { t.HasCheckConstraint("ck_business_media_role", "\"role\" = 'Gallery'"); t.HasCheckConstraint("ck_business_media_order", "\"sort_order\" >= 0"); });
        b.HasKey(x => new { x.BusinessId, x.MediaId });
        b.Property(x => x.BusinessId).HasColumnName("business_id");
        b.Property(x => x.MediaId).HasColumnName("media_id");
        b.Property(x => x.Role).HasColumnName("role").HasMaxLength(20).HasDefaultValue("Gallery");
        b.Property(x => x.SortOrder).HasColumnName("sort_order");
        b.Property(x => x.IsFeatured).HasColumnName("is_featured");
        // Captions are owner-authored display text, capped so a caption cannot
        // become a paragraph the storefront has to lay out.
        b.Property(x => x.Caption).HasColumnName("caption").HasMaxLength(200);
        // Published-by-default: every row that existed before this column was
        // added was already public, so the default keeps them public.
        b.Property(x => x.IsVisible).HasColumnName("is_visible").HasDefaultValue(true);
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.HasIndex(x => new { x.BusinessId, x.SortOrder, x.CreatedAt }).HasDatabaseName("ix_business_media_order");
        b.HasOne(x => x.Business).WithMany().HasForeignKey(x => x.BusinessId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Media).WithMany().HasForeignKey(x => x.MediaId).OnDelete(DeleteBehavior.Restrict);
    }
}
