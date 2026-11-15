using System.ComponentModel.DataAnnotations;

namespace PunchedApi.Domain.Entities;

/// <summary>
/// One import batch of the business's own M-Pesa statement (the CSV Safaricom lets a
/// business download from the M-Pesa portal, or an equivalent export).
///
/// <para><b>Why a batch row exists.</b> Two problems have to be solved together:
/// identical statement lines are legitimate (two customers may each pay KES 500 in the
/// same minute), and re-uploading the same file must not double every line. The batch
/// keeps the file fingerprint (<see cref="ContentHash"/>) so a repeat upload of the same
/// content is refused, while each <see cref="MpesaStatementLine"/> keeps its own identity
/// so the reconciliation state of every line survives.</para>
///
/// <para><b>No Safaricom integration is assumed.</b> Punched never claims a direct link
/// to M-Pesa: this is the business comparing Punched's records with a file it obtained
/// from its own M-Pesa account.</para>
/// </summary>
public class MpesaStatementImport : BaseEntity
{
    /// <summary>Tenant scope; mandatory for isolation.</summary>
    [Required]
    public Guid BusinessId { get; set; }

    /// <summary>The file's name as uploaded (for the business's own records).</summary>
    [Required]
    [MaxLength(260)]
    public string FileName { get; set; } = string.Empty;

    /// <summary>Format parser used: currently <c>csv</c>.</summary>
    [Required]
    [MaxLength(20)]
    public string Format { get; set; } = "csv";

    /// <summary>
    /// SHA-256 of the uploaded content. Unique per business, so the same statement cannot be
    /// imported twice (which would create a duplicate of every line in it).
    /// </summary>
    [Required]
    [MaxLength(64)]
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>How many lines were parsed from the file.</summary>
    public int LineCount { get; set; }

    /// <summary>How many lines matched a Punched transaction at import time.</summary>
    public int MatchedCount { get; set; }

    /// <summary>How many lines were flagged as discrepancies (amount or unknown reference).</summary>
    public int DiscrepancyCount { get; set; }

    /// <summary>How many lines had no counterpart in Punched (money in M-Pesa Punched never recorded).</summary>
    public int UnmatchedCount { get; set; }

    /// <summary>How many lines were excluded from matching (duplicates inside the file).</summary>
    public int ExcludedCount { get; set; }

    /// <summary>Who imported it.</summary>
    public Guid? ImportedByUserId { get; set; }

    /// <summary>UTC time the import ran.</summary>
    [Required]
    public DateTime ImportedAt { get; set; } = DateTime.UtcNow;

    /// <summary>What the importer was told (e.g. lines that could not be parsed).</summary>
    [MaxLength(500)]
    public string? Notes { get; set; }

    // ── Navigation ──────────────────────────────────────────
    public virtual Business Business { get; set; } = null!;

    public virtual ICollection<MpesaStatementLine> Lines { get; set; } = new List<MpesaStatementLine>();
}

/// <summary>
/// One line of an imported M-Pesa statement, with its own reconciliation state. Independent
/// from <see cref="FinancialTransaction"/>: a line exists whether or not Punched recorded the
/// money, which is what makes "in M-Pesa but missing in Punched" detectable.
/// </summary>
public class MpesaStatementLine : BaseEntity
{
    /// <summary>Tenant scope; mandatory for isolation.</summary>
    [Required]
    public Guid BusinessId { get; set; }

    /// <summary>The import batch this line belongs to.</summary>
    [Required]
    public Guid ImportId { get; set; }

    /// <summary>The M-Pesa receipt code (the join key with Punched).</summary>
    [Required]
    [MaxLength(40)]
    public string ReceiptNumber { get; set; } = string.Empty;

    /// <summary>Amount the statement says moved (KES, 2dp).</summary>
    [Required]
    public decimal Amount { get; set; }

    [Required]
    [MaxLength(3)]
    public string Currency { get; set; } = "KES";

    /// <summary>When the statement says the money moved.</summary>
    public DateTime? TransactionAt { get; set; }

    /// <summary>Who paid, as the statement names them.</summary>
    [MaxLength(200)]
    public string? PayerName { get; set; }

    [MaxLength(40)]
    public string? PayerPhone { get; set; }

    /// <summary>The account/order reference the payer quoted (the Punched reference when one was used).</summary>
    [MaxLength(100)]
    public string? AccountReference { get; set; }

    /// <summary>The narrative/details column, verbatim.</summary>
    [MaxLength(500)]
    public string? Narrative { get; set; }

    /// <summary>Reconciliation outcome for this line.</summary>
    [Required]
    public ReconciliationStatus Status { get; set; } = ReconciliationStatus.Unreconciled;

    /// <summary>The Punched ledger row matched to this line, when matched.</summary>
    public Guid? MatchedTransactionId { get; set; }

    /// <summary>The Punched payment matched to this line, when matched against a payment instead of a ledger row.</summary>
    public Guid? MatchedPaymentId { get; set; }

    /// <summary>How the line was matched: <c>reference</c> | <c>amount_date</c> | <c>manual</c>.</summary>
    [MaxLength(20)]
    public string? MatchMethod { get; set; }

    /// <summary>What the reconciler was told about this line (discrepancy detail, exclusion reason).</summary>
    [MaxLength(500)]
    public string? Note { get; set; }

    /// <summary>Set when the line duplicates an earlier receipt inside the same file.</summary>
    public bool IsDuplicateInFile { get; set; }

    [Required]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // ── Navigation ──────────────────────────────────────────
    public virtual MpesaStatementImport Import { get; set; } = null!;
}
