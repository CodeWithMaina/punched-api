using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data.Configurations;

/// <summary>
/// A temporary claim on a staff member's interval while the customer pays.
///
/// <para>The indexes are shaped by the two reads that exist: "which holds block this member's
/// diary between these instants" (availability and the booking write) and "this hold's own state"
/// (the countdown, the conversion and the release). The first is served by
/// (business, staff, start) with a status filter, which is what every availability query asks.</para>
/// </summary>
public class AppointmentSlotHoldConfiguration : IEntityTypeConfiguration<AppointmentSlotHold>
{
    public void Configure(EntityTypeBuilder<AppointmentSlotHold> builder)
    {
        builder.ToTable("appointment_slot_holds");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.BusinessId).HasColumnName("business_id").IsRequired();
        builder.Property(x => x.StaffUserId).HasColumnName("staff_user_id").IsRequired();
        builder.Property(x => x.CustomerUserId).HasColumnName("customer_user_id");
        builder.Property(x => x.StartsAtUtc).HasColumnName("starts_at_utc").IsRequired();
        builder.Property(x => x.EndsAtUtc).HasColumnName("ends_at_utc").IsRequired();
        builder.Property(x => x.ServiceIdsJson).HasColumnName("service_ids_json").HasMaxLength(2000).IsRequired();
        builder.Property(x => x.DurationMinutes).HasColumnName("duration_minutes");
        builder.Property(x => x.CustomerName).HasColumnName("customer_name").HasMaxLength(100);
        builder.Property(x => x.CustomerPhone).HasColumnName("customer_phone").HasMaxLength(40);
        builder.Property(x => x.CustomerEmail).HasColumnName("customer_email").HasMaxLength(200);
        builder.Property(x => x.Note).HasColumnName("note").HasMaxLength(500);
        builder.Property(x => x.PaymentOption).HasColumnName("payment_option").HasMaxLength(30);
        builder.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(100);
        builder.Property(x => x.ExpiresAtUtc).HasColumnName("expires_at_utc").IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(20).IsRequired();
        builder.Property(x => x.ConvertedAppointmentId).HasColumnName("converted_appointment_id");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");

        builder.HasOne<Business>()
            .WithMany()
            .HasForeignKey(x => x.BusinessId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.StaffUserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.CustomerUserId)
            .OnDelete(DeleteBehavior.SetNull);

        // The availability read: this member's claims inside a window, blocking statuses only.
        builder.HasIndex(x => new { x.BusinessId, x.StaffUserId, x.StartsAtUtc })
            .HasDatabaseName("ix_appointment_slot_holds_business_staff_start");

        // The expiry sweep and "what is still live?" — tenant-scoped, oldest deadline first.
        builder.HasIndex(x => new { x.BusinessId, x.Status, x.ExpiresAtUtc })
            .HasDatabaseName("ix_appointment_slot_holds_business_status_expiry");

        // One hold per booking intent, so a refreshed page resumes the claim it already has
        // instead of stacking a second one on the same interval.
        builder.HasIndex(x => new { x.BusinessId, x.IdempotencyKey })
            .IsUnique()
            .HasFilter("\"idempotency_key\" IS NOT NULL")
            .HasDatabaseName("ux_appointment_slot_holds_booking_key");
    }
}
