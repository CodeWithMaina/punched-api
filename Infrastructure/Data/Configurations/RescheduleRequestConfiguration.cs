using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data.Configurations;

/// <summary>
/// <c>reschedule_requests</c> — the rescheduling workflow.
///
/// <para><b>Migration note.</b> This entity previously had no explicit
/// configuration, so EF created it as <c>RescheduleRequests</c> with
/// PascalCase columns. This configuration renames both the table and its
/// columns to the platform's snake_case convention; EF emits data-preserving
/// <c>RenameTable</c>/<c>RenameColumn</c> operations, so the seeded pending and
/// approved requests survive the migration intact.</para>
/// </summary>
public class RescheduleRequestConfiguration : IEntityTypeConfiguration<RescheduleRequest>
{
    public void Configure(EntityTypeBuilder<RescheduleRequest> builder)
    {
        builder.ToTable("reschedule_requests", t =>
        {
            t.HasCheckConstraint(
                "ck_reschedule_requests_status",
                "status IN ('pending', 'approved', 'rejected', 'withdrawn', 'expired')");
            t.HasCheckConstraint(
                "ck_reschedule_requests_party",
                "requested_by_party IN ('Customer', 'Staff', 'Owner')");
            t.HasCheckConstraint(
                "ck_reschedule_requests_window",
                "proposed_end_at > proposed_scheduled_at");
        });

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.AppointmentId).IsRequired().HasColumnName("appointment_id");
        builder.Property(x => x.BusinessId).IsRequired().HasColumnName("business_id");
        builder.Property(x => x.RequestedByUserId).IsRequired().HasColumnName("requested_by_user_id");
        builder.Property(x => x.RequestedByParty)
            .IsRequired().HasConversion<string>().HasMaxLength(20).HasColumnName("requested_by_party");
        builder.Property(x => x.RequestReason).HasMaxLength(300).HasColumnName("request_reason");
        builder.Property(x => x.OriginalScheduledAt).HasColumnName("original_scheduled_at");
        builder.Property(x => x.OriginalEndAt).HasColumnName("original_end_at");
        builder.Property(x => x.OriginalStaffUserId).HasColumnName("original_staff_user_id");
        builder.Property(x => x.ProposedScheduledAt).IsRequired().HasColumnName("proposed_scheduled_at");
        builder.Property(x => x.ProposedEndAt).IsRequired().HasColumnName("proposed_end_at");
        builder.Property(x => x.ProposedStaffUserId).HasColumnName("proposed_staff_user_id");
        builder.Property(x => x.ProposedServicesJson).IsRequired().HasColumnName("proposed_services_json");
        builder.Property(x => x.Status).IsRequired().HasMaxLength(20).HasColumnName("status");
        builder.Property(x => x.ExpiresAt).HasColumnName("expires_at");
        builder.Property(x => x.ResolvedAt).HasColumnName("resolved_at");
        builder.Property(x => x.ResolvedByUserId).HasColumnName("resolved_by_user_id");
        builder.Property(x => x.DecisionNotes).HasMaxLength(500).HasColumnName("decision_notes");
        builder.Property(x => x.UpdatedAt).IsRequired().HasColumnName("updated_at");
        builder.Property(x => x.Version).IsRequired().HasDefaultValue(0).HasColumnName("version");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");

        builder.HasOne<Appointment>()
            .WithMany()
            .HasForeignKey(x => x.AppointmentId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Business>()
            .WithMany()
            .HasForeignKey(x => x.BusinessId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.RequestedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.ResolvedByUserId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.OriginalStaffUserId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.ProposedStaffUserId)
            .OnDelete(DeleteBehavior.SetNull);

        // "Is there an open proposal for this appointment?" — the hot path.
        builder.HasIndex(x => new { x.AppointmentId, x.Status })
            .HasDatabaseName("ix_reschedule_requests_appointment_status");

        // Tenant-scoped operational queues (pending / expiring).
        builder.HasIndex(x => new { x.BusinessId, x.Status, x.CreatedAt })
            .HasDatabaseName("ix_reschedule_requests_business_status_created");

        // Expiry sweep.
        builder.HasIndex(x => new { x.Status, x.ExpiresAt })
            .HasDatabaseName("ix_reschedule_requests_status_expires");
    }
}
