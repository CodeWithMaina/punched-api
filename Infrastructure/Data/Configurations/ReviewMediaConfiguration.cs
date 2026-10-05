using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data.Configurations;

public sealed class ReviewMediaConfiguration : IEntityTypeConfiguration<ReviewMedia>
{
    public void Configure(EntityTypeBuilder<ReviewMedia> b)
    {
        b.ToTable("review_media", t => t.HasCheckConstraint("ck_review_media_order", "\"sort_order\" >= 0"));
        b.HasKey(x => new { x.ReviewId, x.MediaId });
        b.Property(x => x.ReviewId).HasColumnName("review_id");
        b.Property(x => x.MediaId).HasColumnName("media_id");
        b.Property(x => x.SortOrder).HasColumnName("sort_order");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.HasIndex(x => new { x.ReviewId, x.SortOrder, x.CreatedAt }).HasDatabaseName("ix_review_media_order");
        b.HasOne(x => x.Review).WithMany().HasForeignKey(x => x.ReviewId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Media).WithMany().HasForeignKey(x => x.MediaId).OnDelete(DeleteBehavior.Restrict);
    }
}
