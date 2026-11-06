using Microsoft.EntityFrameworkCore;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data;

/// <summary>EF configuration for the payments module. Indexes match the real query patterns:</summary>
/// business dashboards (BusinessId + Status + CreatedAt), customer history (CustomerId),
/// appointment summaries (AppointmentId), callback idempotency (unique EventKey),
/// C2B matching (unique Reference, shortcode lookup).
public class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Payment> builder)
    {
        builder.ToTable("payments");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.Reference).HasMaxLength(40).IsRequired();
        builder.HasIndex(p => p.Reference).IsUnique();

        builder.Property(p => p.Currency).HasMaxLength(3).IsRequired();
        builder.Property(p => p.ExternalReference).HasMaxLength(100);
        builder.Property(p => p.PhoneNumber).HasMaxLength(20);
        builder.Property(p => p.ReversalReason).HasMaxLength(500);

        // ── Manual M-PESA submission & verification ──
        builder.Property(p => p.SubmittedMessage).HasMaxLength(1000);
        builder.Property(p => p.ParsedTransactionCode).HasMaxLength(20);
        builder.Property(p => p.ParsedRecipient).HasMaxLength(200);
        builder.Property(p => p.RejectionReason).HasMaxLength(500);
        builder.Property(p => p.ParsedAmount).HasPrecision(12, 2);

        // A given M-PESA receipt may back exactly one payment. Enforced in the
        // database, not just in the service, so two concurrent submissions of the
        // same code cannot both win a race. Nulls are ignored by PostgreSQL's
        // unique index, so provider-only payments are unaffected.
        builder.HasIndex(p => p.ParsedTransactionCode).IsUnique();

        builder.Property(p => p.Amount).HasPrecision(12, 2);

        // C2B confirmation callbacks update ExternalReference; lookup by receipt.
        builder.HasIndex(p => p.ExternalReference);

        // Query patterns: dashboard (Business+Status+CreatedAt), appointment summary, customer history.
        builder.HasIndex(p => new { p.BusinessId, p.Status, p.CreatedAt });
        builder.HasIndex(p => p.AppointmentId);
        builder.HasIndex(p => p.CustomerId);

        builder.HasOne(p => p.Business)
            .WithMany()
            .HasForeignKey(p => p.BusinessId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(p => p.Appointment)
            .WithMany()
            .HasForeignKey(p => p.AppointmentId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

public class PaymentAttemptConfiguration : IEntityTypeConfiguration<PaymentAttempt>
{
    public void Configure(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<PaymentAttempt> builder)
    {
        builder.ToTable("payment_attempts");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.Status).HasMaxLength(20).IsRequired();
        builder.Property(a => a.CheckoutRequestId).HasMaxLength(100);
        builder.Property(a => a.MerchantRequestId).HasMaxLength(100);
        builder.Property(a => a.ProviderReference).HasMaxLength(100);
        builder.Property(a => a.ErrorCode).HasMaxLength(50);
        builder.Property(a => a.ErrorMessage).HasMaxLength(500);

        // One attempt-number per payment (append-only history).
        builder.HasIndex(a => new { a.PaymentId, a.AttemptNumber }).IsUnique();
        // STK callback correlation.
        builder.HasIndex(a => a.CheckoutRequestId);

        builder.HasOne(a => a.Payment)
            .WithMany(p => p.Attempts)
            .HasForeignKey(a => a.PaymentId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class PaymentCallbackConfiguration : IEntityTypeConfiguration<PaymentCallback>
{
    public void Configure(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<PaymentCallback> builder)
    {
        builder.ToTable("payment_callbacks");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.Kind).HasMaxLength(30).IsRequired();
        builder.Property(c => c.EventKey).HasMaxLength(200).IsRequired();
        builder.Property(c => c.ResultCode).HasMaxLength(50);
        builder.Property(c => c.ResultDescription).HasMaxLength(500);
        builder.Property(c => c.TransactionId).HasMaxLength(100);
        builder.Property(c => c.Outcome).HasMaxLength(20);
        builder.Property(c => c.RawPayload).IsRequired();

        // Idempotency: the same provider callback can be stored exactly once.
        builder.HasIndex(c => c.EventKey).IsUnique();
        // Reconciliation: find unmatched confirmations per business.
        builder.HasIndex(c => new { c.BusinessId, c.Kind, c.Outcome });

        builder.HasOne(c => c.Payment)
            .WithMany()
            .HasForeignKey(c => c.PaymentId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

public class BusinessPaymentConfigConfiguration : IEntityTypeConfiguration<BusinessPaymentConfig>
{
    public void Configure(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<BusinessPaymentConfig> builder)
    {
        builder.ToTable("business_payment_configs");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.MpesaAccountType).HasMaxLength(10);
        builder.Property(c => c.MpesaShortCode).HasMaxLength(20);

        // One payment configuration per business. Tenant isolation for credentials.
        builder.HasIndex(c => c.BusinessId).IsUnique();
        // C2B tenant resolution by shortcode.
        builder.HasIndex(c => c.MpesaShortCode);

        builder.HasOne(c => c.Business)
            .WithMany()
            .HasForeignKey(c => c.BusinessId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class PaymentVerificationEventConfiguration : IEntityTypeConfiguration<PaymentVerificationEvent>
{
    public void Configure(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<PaymentVerificationEvent> builder)
    {
        builder.ToTable("payment_verification_events");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Kind).HasMaxLength(20).IsRequired();
        builder.Property(e => e.ActorName).HasMaxLength(100);
        builder.Property(e => e.TransactionCode).HasMaxLength(20);
        builder.Property(e => e.Message).HasMaxLength(1000);
        builder.Property(e => e.Reason).HasMaxLength(500);
        builder.Property(e => e.Amount).HasPrecision(12, 2);

        // The business's verification history screen: newest first, per tenant.
        builder.HasIndex(e => new { e.BusinessId, e.CreatedAt });
        // One payment's own trail.
        builder.HasIndex(e => e.PaymentId);

        builder.HasOne(e => e.Payment)
            .WithMany(p => p.VerificationEvents)
            .HasForeignKey(e => e.PaymentId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
