using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data.Configurations;

/// <summary>
/// <c>commission_configs</c> — one configuration per business.
/// </summary>
public class CommissionConfigConfiguration : IEntityTypeConfiguration<CommissionConfig>
{
    public void Configure(EntityTypeBuilder<CommissionConfig> builder)
    {
        builder.ToTable("commission_configs", t =>
        {
            t.HasCheckConstraint(
                "ck_commission_configs_calculation_type",
                "calculation_type IN ('Percentage', 'Fixed')");
            t.HasCheckConstraint(
                "ck_commission_configs_percentage_range",
                "default_percentage IS NULL OR (default_percentage >= 0 AND default_percentage <= 100)");
            t.HasCheckConstraint(
                "ck_commission_configs_fixed_non_negative",
                "default_fixed_amount IS NULL OR default_fixed_amount >= 0");
            t.HasCheckConstraint(
                "ck_commission_configs_settlement_ratio",
                "minimum_settlement_ratio >= 0 AND minimum_settlement_ratio <= 1");
            t.HasCheckConstraint(
                "ck_commission_configs_rounding",
                "rounding_decimals >= 0 AND rounding_decimals <= 4");
        });

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.BusinessId).IsRequired().HasColumnName("business_id");
        builder.Property(x => x.IsEnabled).IsRequired().HasColumnName("is_enabled");
        builder.Property(x => x.CalculationType)
            .IsRequired().HasConversion<string>().HasMaxLength(20).HasColumnName("calculation_type");
        builder.Property(x => x.DefaultPercentage).HasPrecision(7, 4).HasColumnName("default_percentage");
        builder.Property(x => x.DefaultFixedAmount).HasPrecision(14, 2).HasColumnName("default_fixed_amount");
        builder.Property(x => x.Currency).IsRequired().HasMaxLength(3).HasColumnName("currency");
        builder.Property(x => x.RequireSettlement).IsRequired().HasColumnName("require_settlement");
        builder.Property(x => x.RequireApproval).IsRequired().HasColumnName("require_approval");
        builder.Property(x => x.EarnOnNoShow).IsRequired().HasColumnName("earn_on_no_show");
        builder.Property(x => x.EarnOnCancelled).IsRequired().HasColumnName("earn_on_cancelled");
        builder.Property(x => x.AllowPartialSettlement).IsRequired().HasColumnName("allow_partial_settlement");
        builder.Property(x => x.MinimumSettlementRatio).HasPrecision(5, 4).HasColumnName("minimum_settlement_ratio");
        builder.Property(x => x.RequireStaffServiceEligibility).IsRequired().HasColumnName("require_staff_service_eligibility");
        builder.Property(x => x.RoundingDecimals).IsRequired().HasColumnName("rounding_decimals");
        builder.Property(x => x.Notes).HasMaxLength(300).HasColumnName("notes");
        builder.Property(x => x.UpdatedAt).IsRequired().HasColumnName("updated_at");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");

        builder.HasOne(x => x.Business)
            .WithMany()
            .HasForeignKey(x => x.BusinessId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => x.BusinessId).IsUnique().HasDatabaseName("ux_commission_configs_business");
    }
}

/// <summary>
/// <c>commission_rates</c> — scoped overrides of the business default.
/// Uniqueness is enforced on the deterministic scope key so a second,
/// conflicting arrangement for the same scope is impossible.
/// </summary>
public class CommissionRateConfiguration : IEntityTypeConfiguration<CommissionRate>
{
    public void Configure(EntityTypeBuilder<CommissionRate> builder)
    {
        builder.ToTable("commission_rates", t =>
        {
            t.HasCheckConstraint(
                "ck_commission_rates_scope",
                "scope IN ('Business', 'Staff', 'Service', 'StaffService')");
            t.HasCheckConstraint(
                "ck_commission_rates_calculation_type",
                "calculation_type IN ('Percentage', 'Fixed')");
            t.HasCheckConstraint(
                "ck_commission_rates_percentage_range",
                "percentage IS NULL OR (percentage >= 0 AND percentage <= 100)");
            t.HasCheckConstraint(
                "ck_commission_rates_fixed_non_negative",
                "fixed_amount IS NULL OR fixed_amount >= 0");
            // A staff-scoped rate must name a staff member; a service-scoped rate
            // must name a service. Prevents a scope whose key lies about its content.
            t.HasCheckConstraint(
                "ck_commission_rates_scope_target",
                "(scope = 'Business' AND staff_user_id IS NULL AND service_id IS NULL) OR " +
                "(scope = 'Staff' AND staff_user_id IS NOT NULL AND service_id IS NULL) OR " +
                "(scope = 'Service' AND staff_user_id IS NULL AND service_id IS NOT NULL) OR " +
                "(scope = 'StaffService' AND staff_user_id IS NOT NULL AND service_id IS NOT NULL)");
        });

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.BusinessId).IsRequired().HasColumnName("business_id");
        builder.Property(x => x.Scope)
            .IsRequired().HasConversion<string>().HasMaxLength(20).HasColumnName("scope");
        builder.Property(x => x.StaffUserId).HasColumnName("staff_user_id");
        builder.Property(x => x.ServiceCatalogItemId).HasColumnName("service_id");
        builder.Property(x => x.ScopeKey).IsRequired().HasMaxLength(120).HasColumnName("scope_key");
        builder.Property(x => x.CalculationType)
            .IsRequired().HasConversion<string>().HasMaxLength(20).HasColumnName("calculation_type");
        builder.Property(x => x.Percentage).HasPrecision(7, 4).HasColumnName("percentage");
        builder.Property(x => x.FixedAmount).HasPrecision(14, 2).HasColumnName("fixed_amount");
        builder.Property(x => x.IsActive).IsRequired().HasColumnName("is_active");
        builder.Property(x => x.UpdatedAt).IsRequired().HasColumnName("updated_at");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");

        builder.HasOne(x => x.Business)
            .WithMany()
            .HasForeignKey(x => x.BusinessId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.StaffUserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<ServiceCatalogItem>()
            .WithMany()
            .HasForeignKey(x => x.ServiceCatalogItemId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => new { x.BusinessId, x.ScopeKey })
            .IsUnique()
            .HasDatabaseName("ux_commission_rates_business_scope");

        builder.HasIndex(x => new { x.BusinessId, x.IsActive })
            .HasDatabaseName("ix_commission_rates_business_active");
    }
}

/// <summary>
/// <c>commission_entries</c> — the immutable commission ledger.
/// </summary>
public class CommissionEntryConfiguration : IEntityTypeConfiguration<CommissionEntry>
{
    public void Configure(EntityTypeBuilder<CommissionEntry> builder)
    {
        builder.ToTable("commission_entries", t =>
        {
            t.HasCheckConstraint(
                "ck_commission_entries_entry_type",
                "entry_type IN ('Earning', 'Adjustment', 'Reversal')");
            t.HasCheckConstraint(
                "ck_commission_entries_status",
                "status IN ('Pending', 'Approved', 'Payable', 'Paid', 'Disputed', 'Reversed', 'Void')");
            t.HasCheckConstraint(
                "ck_commission_entries_calculation_type",
                "calculation_type IN ('Percentage', 'Fixed')");
            t.HasCheckConstraint(
                "ck_commission_entries_config_scope",
                "config_scope IN ('Business', 'Staff', 'Service', 'StaffService')");
            t.HasCheckConstraint(
                "ck_commission_entries_source_type",
                "source_type IN ('Appointment', 'Manual')");
            // Adjustments and reversals must be explainable.
            t.HasCheckConstraint(
                "ck_commission_entries_reason_required",
                "entry_type = 'Earning' OR (reason IS NOT NULL AND length(trim(reason)) > 0)");
        });

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.BusinessId).IsRequired().HasColumnName("business_id");
        builder.Property(x => x.StaffUserId).IsRequired().HasColumnName("staff_user_id");
        builder.Property(x => x.CustomerId).HasColumnName("customer_id");
        builder.Property(x => x.AppointmentId).HasColumnName("appointment_id");
        builder.Property(x => x.AppointmentResourceId).HasColumnName("appointment_resource_id");
        builder.Property(x => x.ServiceCatalogItemId).HasColumnName("service_id");
        builder.Property(x => x.PaymentId).HasColumnName("payment_id");
        builder.Property(x => x.PayoutId).HasColumnName("payout_id");
        builder.Property(x => x.EntryType)
            .IsRequired().HasConversion<string>().HasMaxLength(20).HasColumnName("entry_type");
        builder.Property(x => x.SourceType)
            .IsRequired().HasConversion<string>().HasMaxLength(20).HasColumnName("source_type");
        builder.Property(x => x.Status)
            .IsRequired().HasConversion<string>().HasMaxLength(20).HasColumnName("status");
        builder.Property(x => x.ConfigScope)
            .IsRequired().HasConversion<string>().HasMaxLength(20).HasColumnName("config_scope");
        builder.Property(x => x.ConfigId).HasColumnName("config_id");
        builder.Property(x => x.ConfigSnapshotJson).IsRequired().HasColumnName("config_snapshot_json");
        builder.Property(x => x.CalculationType)
            .IsRequired().HasConversion<string>().HasMaxLength(20).HasColumnName("calculation_type");
        builder.Property(x => x.RatePercent).HasPrecision(7, 4).HasColumnName("rate_percent");
        builder.Property(x => x.FixedAmount).HasPrecision(14, 2).HasColumnName("fixed_amount");
        builder.Property(x => x.BaseAmount).IsRequired().HasPrecision(14, 2).HasColumnName("base_amount");
        builder.Property(x => x.Amount).IsRequired().HasPrecision(14, 2).HasColumnName("amount");
        builder.Property(x => x.Currency).IsRequired().HasMaxLength(3).HasColumnName("currency");
        builder.Property(x => x.Reason).HasMaxLength(300).HasColumnName("reason");
        builder.Property(x => x.AdjustmentOfEntryId).HasColumnName("adjustment_of_entry_id");
        builder.Property(x => x.ReversesEntryId).HasColumnName("reverses_entry_id");
        builder.Property(x => x.ReversedByEntryId).HasColumnName("reversed_by_entry_id");
        builder.Property(x => x.DedupeKey).IsRequired().HasMaxLength(200).HasColumnName("dedupe_key");
        builder.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id");
        builder.Property(x => x.ApprovedAt).HasColumnName("approved_at");
        builder.Property(x => x.ApprovedByUserId).HasColumnName("approved_by_user_id");
        builder.Property(x => x.PayableAt).HasColumnName("payable_at");
        builder.Property(x => x.PaidAt).HasColumnName("paid_at");
        builder.Property(x => x.DisputedAt).HasColumnName("disputed_at");
        builder.Property(x => x.DisputedByUserId).HasColumnName("disputed_by_user_id");
        builder.Property(x => x.DisputeReason).HasMaxLength(300).HasColumnName("dispute_reason");
        builder.Property(x => x.ReversedAt).HasColumnName("reversed_at");
        builder.Property(x => x.VoidedAt).HasColumnName("voided_at");
        builder.Property(x => x.VoidReason).HasMaxLength(300).HasColumnName("void_reason");
        builder.Property(x => x.UpdatedAt).IsRequired().HasColumnName("updated_at");
        builder.Property(x => x.Version).IsRequired().HasDefaultValue(0).HasColumnName("version");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");

        builder.HasOne(x => x.Business)
            .WithMany()
            .HasForeignKey(x => x.BusinessId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.StaffUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.CustomerId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne<Appointment>()
            .WithMany()
            .HasForeignKey(x => x.AppointmentId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne<ServiceCatalogItem>()
            .WithMany()
            .HasForeignKey(x => x.ServiceCatalogItemId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne<Payment>()
            .WithMany()
            .HasForeignKey(x => x.PaymentId)
            .OnDelete(DeleteBehavior.SetNull);

        // The idempotency guarantee: one entry per (event, economic line, entry type).
        builder.HasIndex(x => x.DedupeKey)
            .IsUnique()
            .HasDatabaseName("ux_commission_entries_dedupe_key");

        builder.HasIndex(x => new { x.BusinessId, x.Status, x.CreatedAt })
            .HasDatabaseName("ix_commission_entries_business_status_created");

        builder.HasIndex(x => new { x.BusinessId, x.StaffUserId, x.Status })
            .HasDatabaseName("ix_commission_entries_business_staff_status");

        builder.HasIndex(x => x.AppointmentId)
            .HasDatabaseName("ix_commission_entries_appointment");

        builder.HasIndex(x => x.PayoutId)
            .HasDatabaseName("ix_commission_entries_payout");
    }
}

/// <summary>
/// <c>commission_payouts</c> — recorded staff payouts (attribution only).
/// </summary>
public class CommissionPayoutConfiguration : IEntityTypeConfiguration<CommissionPayout>
{
    public void Configure(EntityTypeBuilder<CommissionPayout> builder)
    {
        builder.ToTable("commission_payouts", t =>
        {
            t.HasCheckConstraint("ck_commission_payouts_total_non_negative", "total_amount >= 0");
            t.HasCheckConstraint("ck_commission_payouts_entry_count", "entry_count >= 0");
        });

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.BusinessId).IsRequired().HasColumnName("business_id");
        builder.Property(x => x.StaffUserId).IsRequired().HasColumnName("staff_user_id");
        builder.Property(x => x.Reference).IsRequired().HasMaxLength(100).HasColumnName("reference");
        builder.Property(x => x.Notes).HasMaxLength(500).HasColumnName("notes");
        builder.Property(x => x.TotalAmount).IsRequired().HasPrecision(14, 2).HasColumnName("total_amount");
        builder.Property(x => x.Currency).IsRequired().HasMaxLength(3).HasColumnName("currency");
        builder.Property(x => x.EntryCount).IsRequired().HasColumnName("entry_count");
        builder.Property(x => x.PeriodStartUtc).HasColumnName("period_start_utc");
        builder.Property(x => x.PeriodEndUtc).HasColumnName("period_end_utc");
        builder.Property(x => x.PaidAtUtc).IsRequired().HasColumnName("paid_at_utc");
        builder.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");

        builder.HasOne(x => x.Business)
            .WithMany()
            .HasForeignKey(x => x.BusinessId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.StaffUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(x => new { x.BusinessId, x.Reference })
            .IsUnique()
            .HasDatabaseName("ux_commission_payouts_business_reference");

        builder.HasIndex(x => new { x.BusinessId, x.StaffUserId, x.PaidAtUtc })
            .HasDatabaseName("ix_commission_payouts_business_staff_paid");
    }
}
