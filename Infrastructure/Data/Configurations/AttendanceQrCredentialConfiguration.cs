using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data.Configurations;

/// <summary>
/// Maps <see cref="AttendanceQrCredential"/> to
/// <c>attendance_qr_credentials</c>. At most one live credential per
/// location, enforced by a partial unique index (the
/// <c>ix_subscription_plans_one_default</c> trick).
/// </summary>
public class AttendanceQrCredentialConfiguration : IEntityTypeConfiguration<AttendanceQrCredential>
{
    public void Configure(EntityTypeBuilder<AttendanceQrCredential> builder)
    {
        builder.ToTable("attendance_qr_credentials");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.BusinessId).HasColumnName("business_id");
        builder.Property(x => x.AttendanceLocationId).HasColumnName("attendance_location_id");
        builder.Property(x => x.TokenHash)
            .IsRequired()
            .HasMaxLength(64)
            .HasColumnName("token_hash");
        builder.Property(x => x.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(20);
        builder.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id");
        builder.Property(x => x.RevokedAt).HasColumnName("revoked_at");
        builder.Property(x => x.RevokedByUserId).HasColumnName("revoked_by_user_id");
        builder.Property(x => x.LastUsedAt).HasColumnName("last_used_at");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");

        // Hot-path hash lookup.
        builder.HasIndex(x => x.TokenHash)
            .IsUnique()
            .HasDatabaseName("ix_attendance_qr_credentials_token_hash");

        // At most one live QR per location.
        builder.HasIndex(x => x.AttendanceLocationId)
            .IsUnique()
            .HasDatabaseName("ix_attendance_qr_credentials_one_active_per_location")
            .HasFilter("\"status\" = 'Active'");

        builder.HasIndex(x => new { x.BusinessId, x.Status })
            .HasDatabaseName("ix_attendance_qr_credentials_business");

        builder.HasOne<Business>()
            .WithMany()
            .HasForeignKey(x => x.BusinessId)
            .OnDelete(DeleteBehavior.Restrict);

        // Deleting a location removes only its credentials; event history
        // keeps its own Restrict location FK, so usage is never orphaned.
        builder.HasOne<AttendanceLocation>()
            .WithMany()
            .HasForeignKey(x => x.AttendanceLocationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
