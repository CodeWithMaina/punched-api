using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data.Configurations;

/// <summary>
/// Fluent API configuration for <see cref="CardAsset"/>.
///
/// Integrity guarantees encoded at the database level (§19):
/// <list type="bullet">
/// <item><c>storage_key</c> is unique, so two rows can never point at the same
/// payload (and a key can never be silently shared across tenants).</item>
/// <item><c>size_bytes</c>, <c>width</c> and <c>height</c> are strictly positive —
/// a zero-byte, zero-pixel "image" is rejected by the database, not just by the
/// service.</item>
/// <item>Business deletion cascades (assets cannot outlive their tenant), while the
/// uploading user is <c>SET NULL</c> so deleting a staff member does not destroy
/// the business's branding.</item>
/// </list>
/// </summary>
public class CardAssetConfiguration : IEntityTypeConfiguration<CardAsset>
{
    public void Configure(EntityTypeBuilder<CardAsset> builder)
    {
        builder.ToTable("card_assets", t =>
        {
            t.HasCheckConstraint("ck_card_assets_size_positive", "\"size_bytes\" > 0");
            t.HasCheckConstraint("ck_card_assets_width_positive", "\"width\" > 0");
            t.HasCheckConstraint("ck_card_assets_height_positive", "\"height\" > 0");
            t.HasCheckConstraint(
                "ck_card_assets_deleted_at_consistent",
                "(\"status\" = 0 AND \"deleted_at\" IS NULL) OR (\"status\" = 1 AND \"deleted_at\" IS NOT NULL)");
        });

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id");

        builder.Property(e => e.BusinessId).IsRequired().HasColumnName("business_id");
        builder.Property(e => e.UploadedByUserId).HasColumnName("uploaded_by_user_id");

        builder.Property(e => e.Purpose)
            .IsRequired()
            .HasMaxLength(40)
            .HasColumnName("purpose")
            .HasDefaultValue(CardAssetPurposes.Artwork);

        builder.Property(e => e.Kind)
            .IsRequired()
            .HasColumnName("kind")
            .HasDefaultValue(CardAssetKind.Image);

        builder.Property(e => e.ContentType).IsRequired().HasMaxLength(60).HasColumnName("content_type");
        builder.Property(e => e.FileExtension).IsRequired().HasMaxLength(10).HasColumnName("file_extension");
        builder.Property(e => e.SizeBytes).IsRequired().HasColumnName("size_bytes");
        builder.Property(e => e.Width).IsRequired().HasColumnName("width");
        builder.Property(e => e.Height).IsRequired().HasColumnName("height");

        builder.Property(e => e.StorageKey).IsRequired().HasMaxLength(300).HasColumnName("storage_key");
        builder.Property(e => e.Sha256).IsRequired().HasMaxLength(64).HasColumnName("sha256");

        builder.Property(e => e.Status)
            .IsRequired()
            .HasColumnName("status")
            .HasDefaultValue(CardAssetStatus.Active);

        builder.Property(e => e.OriginalFileName).HasMaxLength(255).HasColumnName("original_file_name");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at");
        builder.Property(e => e.UpdatedAt).HasColumnName("updated_at");
        builder.Property(e => e.DeletedAt).HasColumnName("deleted_at");

        // A storage key identifies exactly one payload.
        builder.HasIndex(e => e.StorageKey)
            .IsUnique()
            .HasDatabaseName("ux_card_assets_storage_key");

        // Primary read pattern: "this business's live assets of a given purpose"
        // (the asset picker) and "this business's assets, newest first" (the library).
        builder.HasIndex(e => new { e.BusinessId, e.Purpose, e.Status })
            .HasDatabaseName("ix_card_assets_business_purpose_status");

        builder.HasIndex(e => new { e.BusinessId, e.CreatedAt })
            .HasDatabaseName("ix_card_assets_business_created");

        builder.HasOne(e => e.Business)
            .WithMany()
            .HasForeignKey(e => e.BusinessId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(e => e.UploadedByUser)
            .WithMany()
            .HasForeignKey(e => e.UploadedByUserId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
