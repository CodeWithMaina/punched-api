using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data;

/// <summary>
/// EF configuration for the financial ledger. Indexes mirror the real query shapes: the
/// owner's filtered transaction list (BusinessId + TransactionDate), the cashbook period
/// scan, expense reporting (BusinessId + Type + Category + TransactionDate), revenue-source
/// breakdowns, and the idempotency guarantees — one row per settled payment (unique
/// <c>source_payment_id</c>) and one ledger reference per business.
/// </summary>
public class FinancialTransactionConfiguration : IEntityTypeConfiguration<FinancialTransaction>
{
    public void Configure(EntityTypeBuilder<FinancialTransaction> builder)
    {
        builder.ToTable("financial_transactions");
        builder.HasKey(t => t.Id);

        // Column names are snake_case, like every other table in this schema: the naming is declared
        // explicitly here rather than left to EF's PascalCase default, so a hand-written migration and
        // the model cannot drift apart (which is exactly how the first version of this migration ended
        // up referencing businesses."Id" when the column is businesses."id").
        builder.Property(t => t.Id).HasColumnName("id");
        builder.Property(t => t.BusinessId).IsRequired().HasColumnName("business_id");
        builder.Property(t => t.Type).IsRequired().HasColumnName("type");
        builder.Property(t => t.Source).IsRequired().HasColumnName("source");
        builder.Property(t => t.PaymentMethod).IsRequired().HasColumnName("payment_method");
        builder.Property(t => t.VerificationStatus).IsRequired().HasColumnName("verification_status");
        builder.Property(t => t.ReconciliationStatus).IsRequired().HasColumnName("reconciliation_status");
        builder.Property(t => t.Currency).HasMaxLength(3).IsRequired().HasColumnName("currency");
        builder.Property(t => t.Reference).HasMaxLength(40).IsRequired().HasColumnName("reference");
        builder.Property(t => t.ExternalReference).HasMaxLength(100).HasColumnName("external_reference");
        builder.Property(t => t.Category).HasMaxLength(40).HasColumnName("category");
        builder.Property(t => t.Description).HasMaxLength(500).HasColumnName("description");
        builder.Property(t => t.CustomerId).HasColumnName("customer_id");
        builder.Property(t => t.CustomerName).HasMaxLength(200).HasColumnName("customer_name");
        builder.Property(t => t.AppointmentId).HasColumnName("appointment_id");
        builder.Property(t => t.SourcePaymentId).HasColumnName("source_payment_id");
        builder.Property(t => t.RefundOfTransactionId).HasColumnName("refund_of_transaction_id");
        builder.Property(t => t.RefundReason).HasMaxLength(500).HasColumnName("refund_reason");
        builder.Property(t => t.RefundReference).HasMaxLength(100).HasColumnName("refund_reference");
        builder.Property(t => t.TransactionDate).IsRequired().HasColumnName("transaction_date");
        builder.Property(t => t.RecordedAt).IsRequired().HasColumnName("recorded_at");
        builder.Property(t => t.VerifiedAt).HasColumnName("verified_at");
        builder.Property(t => t.VerifiedByUserId).HasColumnName("verified_by_user_id");
        builder.Property(t => t.RejectionReason).HasMaxLength(500).HasColumnName("rejection_reason");
        builder.Property(t => t.CreatedByUserId).HasColumnName("created_by_user_id");
        builder.Property(t => t.UpdatedAt).IsRequired().HasColumnName("updated_at");
        builder.Property(t => t.IsVoided).IsRequired().HasColumnName("is_voided");
        builder.Property(t => t.VoidedAt).HasColumnName("voided_at");
        builder.Property(t => t.VoidedByUserId).HasColumnName("voided_by_user_id");
        builder.Property(t => t.VoidReason).HasMaxLength(500).HasColumnName("void_reason");
        builder.Property(t => t.ReconciledAt).HasColumnName("reconciled_at");
        builder.Property(t => t.ReconciledByUserId).HasColumnName("reconciled_by_user_id");
        builder.Property(t => t.StatementLineId).HasColumnName("statement_line_id");
        builder.Property(t => t.ReconciliationNote).HasMaxLength(500).HasColumnName("reconciliation_note");
        builder.Property(t => t.CreatedAt).HasColumnName("created_at");

        // Money precision: KES amounts never need more than 2dp, and a wide-enough integer
        // part keeps annual totals safe.
        builder.Property(t => t.Amount).HasPrecision(14, 2).HasColumnName("amount");

        // ── Idempotency and uniqueness ──
        // The Punched-issued reference is unique across the platform, and so is the payment a row
        // recognises. PostgreSQL ignores NULLs in a unique index, so standalone income (no payment)
        // is unaffected while a second attempt to recognise the same payment fails.
        builder.HasIndex(t => t.Reference).IsUnique();
        builder.HasIndex(t => t.SourcePaymentId).IsUnique();

        // ── Query patterns ──
        builder.HasIndex(t => new { t.BusinessId, t.TransactionDate });
        builder.HasIndex(t => new { t.BusinessId, t.Type, t.VerificationStatus, t.TransactionDate });
        builder.HasIndex(t => new { t.BusinessId, t.Category, t.TransactionDate });
        builder.HasIndex(t => t.AppointmentId);
        builder.HasIndex(t => t.CustomerId);
        builder.HasIndex(t => t.RefundOfTransactionId);
        builder.HasIndex(t => new { t.BusinessId, t.ReconciliationStatus });

        builder.HasOne(t => t.Business)
            .WithMany()
            .HasForeignKey(t => t.BusinessId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>EF configuration for the financial audit trail (append-only, newest-first reads per tenant).</summary>
public class FinancialAuditLogConfiguration : IEntityTypeConfiguration<FinancialAuditLog>
{
    public void Configure(EntityTypeBuilder<FinancialAuditLog> builder)
    {
        builder.ToTable("financial_audit_logs");
        builder.HasKey(l => l.Id);

        builder.Property(l => l.Id).HasColumnName("id");
        builder.Property(l => l.BusinessId).IsRequired().HasColumnName("business_id");
        builder.Property(l => l.EntityType).HasMaxLength(40).IsRequired().HasColumnName("entity_type");
        builder.Property(l => l.EntityId).IsRequired().HasColumnName("entity_id");
        builder.Property(l => l.Action).HasMaxLength(40).IsRequired().HasColumnName("action");
        builder.Property(l => l.ActorUserId).HasColumnName("actor_user_id");
        builder.Property(l => l.ActorName).HasMaxLength(100).HasColumnName("actor_name");
        builder.Property(l => l.Note).HasMaxLength(500).HasColumnName("note");
        builder.Property(l => l.ChangesJson).HasColumnName("changes_json");
        builder.Property(l => l.Reference).HasMaxLength(40).HasColumnName("reference");
        builder.Property(l => l.CreatedAt).HasColumnName("created_at");

        builder.HasIndex(l => new { l.BusinessId, l.CreatedAt });
        builder.HasIndex(l => new { l.EntityType, l.EntityId });
    }
}

/// <summary>EF configuration for M-Pesa statement imports and their lines.</summary>
public class MpesaStatementImportConfiguration : IEntityTypeConfiguration<MpesaStatementImport>
{
    public void Configure(EntityTypeBuilder<MpesaStatementImport> builder)
    {
        builder.ToTable("mpesa_statement_imports");
        builder.HasKey(i => i.Id);

        builder.Property(i => i.Id).HasColumnName("id");
        builder.Property(i => i.BusinessId).IsRequired().HasColumnName("business_id");
        builder.Property(i => i.FileName).HasMaxLength(260).IsRequired().HasColumnName("file_name");
        builder.Property(i => i.Format).HasMaxLength(20).IsRequired().HasColumnName("format");
        builder.Property(i => i.ContentHash).HasMaxLength(64).IsRequired().HasColumnName("content_hash");
        builder.Property(i => i.LineCount).IsRequired().HasColumnName("line_count");
        builder.Property(i => i.MatchedCount).IsRequired().HasColumnName("matched_count");
        builder.Property(i => i.DiscrepancyCount).IsRequired().HasColumnName("discrepancy_count");
        builder.Property(i => i.UnmatchedCount).IsRequired().HasColumnName("unmatched_count");
        builder.Property(i => i.ExcludedCount).IsRequired().HasColumnName("excluded_count");
        builder.Property(i => i.ImportedByUserId).HasColumnName("imported_by_user_id");
        builder.Property(i => i.ImportedAt).IsRequired().HasColumnName("imported_at");
        builder.Property(i => i.Notes).HasMaxLength(500).HasColumnName("notes");
        builder.Property(i => i.CreatedAt).HasColumnName("created_at");

        // One import per distinct file content per business: re-uploading the same statement
        // cannot duplicate its lines.
        builder.HasIndex(i => new { i.BusinessId, i.ContentHash }).IsUnique();

        builder.HasOne(i => i.Business)
            .WithMany()
            .HasForeignKey(i => i.BusinessId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>EF configuration for imported statement lines.</summary>
public class MpesaStatementLineConfiguration : IEntityTypeConfiguration<MpesaStatementLine>
{
    public void Configure(EntityTypeBuilder<MpesaStatementLine> builder)
    {
        builder.ToTable("mpesa_statement_lines");
        builder.HasKey(l => l.Id);

        builder.Property(l => l.Id).HasColumnName("id");
        builder.Property(l => l.BusinessId).IsRequired().HasColumnName("business_id");
        builder.Property(l => l.ImportId).IsRequired().HasColumnName("import_id");
        builder.Property(l => l.ReceiptNumber).HasMaxLength(40).IsRequired().HasColumnName("receipt_number");
        builder.Property(l => l.Currency).HasMaxLength(3).IsRequired().HasColumnName("currency");
        builder.Property(l => l.TransactionAt).HasColumnName("transaction_at");
        builder.Property(l => l.PayerName).HasMaxLength(200).HasColumnName("payer_name");
        builder.Property(l => l.PayerPhone).HasMaxLength(40).HasColumnName("payer_phone");
        builder.Property(l => l.AccountReference).HasMaxLength(100).HasColumnName("account_reference");
        builder.Property(l => l.Narrative).HasMaxLength(500).HasColumnName("narrative");
        builder.Property(l => l.Status).IsRequired().HasColumnName("status");
        builder.Property(l => l.MatchedTransactionId).HasColumnName("matched_transaction_id");
        builder.Property(l => l.MatchedPaymentId).HasColumnName("matched_payment_id");
        builder.Property(l => l.MatchMethod).HasMaxLength(20).HasColumnName("match_method");
        builder.Property(l => l.Note).HasMaxLength(500).HasColumnName("note");
        builder.Property(l => l.IsDuplicateInFile).IsRequired().HasColumnName("is_duplicate_in_file");
        builder.Property(l => l.UpdatedAt).IsRequired().HasColumnName("updated_at");
        builder.Property(l => l.CreatedAt).HasColumnName("created_at");
        builder.Property(l => l.Amount).HasPrecision(14, 2).HasColumnName("amount");

        // Line listings per import, plus the receipt lookup matching performs. Deliberately
        // NOT unique: the same receipt legitimately reappears when a payment was reversed and
        // re-sent, and a genuinely duplicated receipt is a finding the reconciler must SEE
        // rather than a constraint violation that aborts the whole import.
        builder.HasIndex(l => new { l.ImportId, l.ReceiptNumber });
        builder.HasIndex(l => new { l.BusinessId, l.Status });

        builder.HasOne(l => l.Import)
            .WithMany(i => i.Lines)
            .HasForeignKey(l => l.ImportId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

