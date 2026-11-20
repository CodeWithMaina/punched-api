using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data;

/// <summary>
/// EF configuration for the payment → ledger outbox.
///
/// <para>The two indexes are the ones the worker and the operational views actually use:
/// <c>(status, next_attempt_at)</c> for claiming due work and <c>(business_id, status)</c> for a
/// tenant-scoped view of outstanding obligations. <c>payment_id</c> is unique — that constraint,
/// not an application check, is what makes the obligation (and therefore the ledger row) exactly
/// once per payment.</para>
/// </summary>
public class PaymentLedgerObligationConfiguration : IEntityTypeConfiguration<PaymentLedgerObligation>
{
    public void Configure(EntityTypeBuilder<PaymentLedgerObligation> builder)
    {
        builder.ToTable("payment_ledger_obligations");
        builder.HasKey(o => o.Id);

        builder.Property(o => o.Id).HasColumnName("id");
        builder.Property(o => o.PaymentId).IsRequired().HasColumnName("payment_id");
        builder.Property(o => o.BusinessId).IsRequired().HasColumnName("business_id");
        builder.Property(o => o.Status).HasMaxLength(20).IsRequired().HasColumnName("status");
        builder.Property(o => o.Attempts).IsRequired().HasColumnName("attempts");
        builder.Property(o => o.LastAttemptAt).HasColumnName("last_attempt_at");
        builder.Property(o => o.NextAttemptAt).IsRequired().HasColumnName("next_attempt_at");
        builder.Property(o => o.LockedAt).HasColumnName("locked_at");
        builder.Property(o => o.CompletedAt).HasColumnName("completed_at");
        builder.Property(o => o.FinancialTransactionId).HasColumnName("financial_transaction_id");
        builder.Property(o => o.FailureKind).HasMaxLength(20).HasColumnName("failure_kind");
        builder.Property(o => o.LastErrorCode).HasMaxLength(60).HasColumnName("last_error_code");
        builder.Property(o => o.LastError).HasMaxLength(500).HasColumnName("last_error");
        builder.Property(o => o.CreatedAt).IsRequired().HasColumnName("created_at");
        builder.Property(o => o.UpdatedAt).IsRequired().HasColumnName("updated_at");

        // ONE obligation per payment. This is the idempotency key of the whole mechanism.
        builder.HasIndex(o => o.PaymentId).IsUnique().HasDatabaseName("ux_payment_ledger_obligations_payment");

        // Claim query: due pending work, oldest first.
        builder.HasIndex(o => new { o.Status, o.NextAttemptAt })
            .HasDatabaseName("ix_payment_ledger_obligations_claim");

        // Operational view: a business's outstanding work.
        builder.HasIndex(o => new { o.BusinessId, o.Status })
            .HasDatabaseName("ix_payment_ledger_obligations_business_status");

        // Financial history must not disappear with a payment row: restrict deletes.
        builder.HasOne(o => o.Payment)
            .WithMany()
            .HasForeignKey(o => o.PaymentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(o => o.Business)
            .WithMany()
            .HasForeignKey(o => o.BusinessId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
