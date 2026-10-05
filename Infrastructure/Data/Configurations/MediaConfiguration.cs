using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data.Configurations;

public sealed class MediaConfiguration : IEntityTypeConfiguration<Media>
{
    public void Configure(EntityTypeBuilder<Media> b)
    {
        b.ToTable("media", t =>
        {
            t.HasCheckConstraint("ck_media_owner_xor", "(\"business_id\" IS NULL) <> (\"owner_user_id\" IS NULL)");
            t.HasCheckConstraint("ck_media_status", "\"status\" IN (0,1,2,3,4,5,6)");
            t.HasCheckConstraint("ck_media_visibility", "\"visibility\" IN (0,1)");
            t.HasCheckConstraint("ck_media_provider_r2", "\"provider\" = 'r2'");
            t.HasCheckConstraint("ck_media_expected_size", "\"expected_size_bytes\" IS NULL OR \"expected_size_bytes\" > 0");
            t.HasCheckConstraint("ck_media_source_size", "\"source_size_bytes\" IS NULL OR \"source_size_bytes\" > 0");
            t.HasCheckConstraint("ck_media_dimensions", "(\"width\" IS NULL OR \"width\" > 0) AND (\"height\" IS NULL OR \"height\" > 0)");
            t.HasCheckConstraint("ck_media_attempts", "\"upload_attempt\" >= 1 AND \"processing_attempts\" >= 0");
            t.HasCheckConstraint("ck_media_purge_status", "\"delivery_purge_status\" IN (0,1,2,3)");
            t.HasCheckConstraint("ck_media_purge_attempts", "\"delivery_purge_attempts\" >= 0");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.BusinessId).HasColumnName("business_id");
        b.Property(x => x.OwnerUserId).HasColumnName("owner_user_id");
        b.Property(x => x.UploadedByUserId).HasColumnName("uploaded_by_user_id");
        b.Property(x => x.Purpose).HasColumnName("purpose").HasMaxLength(40).IsRequired();
        b.Property(x => x.Provider).HasColumnName("provider").HasMaxLength(20).HasDefaultValue("r2").IsRequired();
        b.Property(x => x.SourceKey).HasColumnName("source_key").HasMaxLength(400).IsRequired();
        b.Property(x => x.Status).HasColumnName("status").HasConversion<int>();
        b.Property(x => x.Visibility).HasColumnName("visibility").HasConversion<int>();
        b.Property(x => x.UploadGrantExpiresAt).HasColumnName("upload_grant_expires_at");
        b.Property(x => x.UploadAttempt).HasColumnName("upload_attempt").HasDefaultValue(1);
        b.Property(x => x.ExpectedSizeBytes).HasColumnName("expected_size_bytes");
        b.Property(x => x.DeclaredMimeType).HasColumnName("declared_mime_type").HasMaxLength(100);
        b.Property(x => x.DetectedMimeType).HasColumnName("detected_mime_type").HasMaxLength(100);
        b.Property(x => x.SourceSizeBytes).HasColumnName("source_size_bytes");
        b.Property(x => x.Width).HasColumnName("width");
        b.Property(x => x.Height).HasColumnName("height");
        b.Property(x => x.Sha256).HasColumnName("sha256").HasMaxLength(64);
        b.Property(x => x.OriginalFileName).HasColumnName("original_file_name").HasMaxLength(255);
        b.Property(x => x.ProcessingLeaseToken).HasColumnName("processing_lease_token").HasMaxLength(64);
        b.Property(x => x.ProcessingLeaseUntil).HasColumnName("processing_lease_until");
        b.Property(x => x.ProcessingAttempts).HasColumnName("processing_attempts");
        b.Property(x => x.NextAttemptAt).HasColumnName("next_attempt_at");
        b.Property(x => x.LastErrorCode).HasColumnName("last_error_code").HasMaxLength(80);
        b.Property(x => x.ProcessingRecipe).HasColumnName("processing_recipe").HasMaxLength(80);
        b.Property(x => x.VariantsJson).HasColumnName("variants_json").HasDefaultValue("[]").IsRequired();
        b.Property(x => x.DeliveryPurgeStatus).HasColumnName("delivery_purge_status").HasConversion<int>();
        b.Property(x => x.DeliveryPurgeAttempts).HasColumnName("delivery_purge_attempts");
        b.Property(x => x.DeliveryPurgeNextAttemptAt).HasColumnName("delivery_purge_next_attempt_at");
        b.Property(x => x.DeliveryPurgeErrorCode).HasColumnName("delivery_purge_error_code").HasMaxLength(80);
        b.Property(x => x.DeliveryPurgedAt).HasColumnName("delivery_purged_at");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        b.Property(x => x.DeletedAt).HasColumnName("deleted_at");
        b.HasIndex(x => x.SourceKey).IsUnique().HasDatabaseName("ux_media_source_key");
        b.HasIndex(x => new { x.BusinessId, x.Status, x.Purpose, x.CreatedAt }).HasDatabaseName("ix_media_business_status_purpose_created");
        b.HasIndex(x => new { x.OwnerUserId, x.Status, x.Purpose, x.CreatedAt }).HasDatabaseName("ix_media_owner_status_purpose_created");
        b.HasIndex(x => new { x.UploadedByUserId, x.CreatedAt }).HasDatabaseName("ix_media_uploader_created");
        b.HasIndex(x => new { x.Status, x.UploadGrantExpiresAt, x.NextAttemptAt, x.ProcessingLeaseUntil }).HasDatabaseName("ix_media_cleanup");
        b.HasIndex(x => new { x.DeliveryPurgeStatus, x.DeliveryPurgeNextAttemptAt }).HasDatabaseName("ix_media_delivery_purge");
        b.HasOne(x => x.Business).WithMany().HasForeignKey(x => x.BusinessId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.OwnerUser).WithMany().HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.UploadedByUser).WithMany().HasForeignKey(x => x.UploadedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}
