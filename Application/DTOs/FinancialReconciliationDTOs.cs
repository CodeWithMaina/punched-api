using System.Text.Json.Serialization;

namespace PunchedApi.Application.DTOs;

/// <summary>One line of an imported M-Pesa statement, as presented to the reconciler.</summary>
public class MpesaStatementLineResponse
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("importId")]
    public Guid ImportId { get; set; }

    [JsonPropertyName("receiptNumber")]
    public string ReceiptNumber { get; set; } = string.Empty;

    [JsonPropertyName("amount")]
    public decimal Amount { get; set; }

    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "KES";

    [JsonPropertyName("transactionAt")]
    public DateTime? TransactionAt { get; set; }

    [JsonPropertyName("payerName")]
    public string? PayerName { get; set; }

    [JsonPropertyName("payerPhone")]
    public string? PayerPhone { get; set; }

    [JsonPropertyName("accountReference")]
    public string? AccountReference { get; set; }

    [JsonPropertyName("narrative")]
    public string? Narrative { get; set; }

    /// <summary><c>unreconciled</c> | <c>matched</c> | <c>discrepancy</c> | <c>excluded</c>.</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("matchedTransactionId")]
    public Guid? MatchedTransactionId { get; set; }

    [JsonPropertyName("matchedPaymentId")]
    public Guid? MatchedPaymentId { get; set; }

    [JsonPropertyName("matchMethod")]
    public string? MatchMethod { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }

    [JsonPropertyName("isDuplicateInFile")]
    public bool IsDuplicateInFile { get; set; }

    /// <summary>What Punched recorded for the same receipt, when it did (the amount comparison).</summary>
    [JsonPropertyName("punchedAmount")]
    public decimal? PunchedAmount { get; set; }

    /// <summary>Punched's ledger reference for the matched transaction, when matched.</summary>
    [JsonPropertyName("punchedReference")]
    public string? PunchedReference { get; set; }

    /// <summary>Statement amount minus Punched's amount, when both exist.</summary>
    [JsonPropertyName("amountVariance")]
    public decimal? AmountVariance { get; set; }
}

/// <summary>An import batch header plus what it produced.</summary>
public class MpesaStatementImportResponse
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("format")]
    public string Format { get; set; } = "csv";

    [JsonPropertyName("importedAt")]
    public DateTime ImportedAt { get; set; }

    [JsonPropertyName("importedByUserId")]
    public Guid? ImportedByUserId { get; set; }

    [JsonPropertyName("lineCount")]
    public int LineCount { get; set; }

    [JsonPropertyName("matchedCount")]
    public int MatchedCount { get; set; }

    [JsonPropertyName("discrepancyCount")]
    public int DiscrepancyCount { get; set; }

    [JsonPropertyName("unmatchedCount")]
    public int UnmatchedCount { get; set; }

    [JsonPropertyName("excludedCount")]
    public int ExcludedCount { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }
}

/// <summary>
/// A transaction Punched recorded that the imported statement does not contain — money in the
/// books with no M-Pesa evidence behind it.
/// </summary>
public class UnreconciledTransactionResponse
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("reference")]
    public string Reference { get; set; } = string.Empty;

    [JsonPropertyName("externalReference")]
    public string? ExternalReference { get; set; }

    [JsonPropertyName("amount")]
    public decimal Amount { get; set; }

    [JsonPropertyName("paymentMethod")]
    public string PaymentMethod { get; set; } = string.Empty;

    [JsonPropertyName("verificationStatus")]
    public string VerificationStatus { get; set; } = string.Empty;

    [JsonPropertyName("transactionDate")]
    public DateTime TransactionDate { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("customerName")]
    public string? CustomerName { get; set; }
}

/// <summary>One entry of a financial record's audit trail.</summary>
public class FinancialAuditLogResponse
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("entityType")]
    public string EntityType { get; set; } = string.Empty;

    [JsonPropertyName("entityId")]
    public Guid EntityId { get; set; }

    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    [JsonPropertyName("actorUserId")]
    public Guid? ActorUserId { get; set; }

    [JsonPropertyName("actorName")]
    public string? ActorName { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }

    [JsonPropertyName("changesJson")]
    public string? ChangesJson { get; set; }

    [JsonPropertyName("reference")]
    public string? Reference { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }
}

/// <summary>The reconciliation workspace: the latest import, its lines, and both sides' loose ends.</summary>
public class ReconciliationWorkspaceResponse
{
    /// <summary>The most recent import batch, or null when nothing has been imported yet.</summary>
    [JsonPropertyName("import")]
    public MpesaStatementImportResponse? Import { get; set; }

    [JsonPropertyName("lines")]
    public List<MpesaStatementLineResponse> Lines { get; set; } = new();

    [JsonPropertyName("lineTotal")]
    public int LineTotal { get; set; }

    /// <summary>Imported lines with no counterpart in Punched (in M-Pesa, missing here).</summary>
    [JsonPropertyName("unmatchedLines")]
    public int UnmatchedLines { get; set; }

    /// <summary>Imported lines whose amount disagrees with Punched, or duplicated receipts.</summary>
    [JsonPropertyName("discrepancyLines")]
    public int DiscrepancyLines { get; set; }

    [JsonPropertyName("matchedLines")]
    public int MatchedLines { get; set; }

    /// <summary>Transactions Punched recorded that the statement does not contain.</summary>
    [JsonPropertyName("transactionsMissingFromStatement")]
    public List<UnreconciledTransactionResponse> TransactionsMissingFromStatement { get; set; } = new();

    [JsonPropertyName("transactionsMissingFromStatementCount")]
    public int TransactionsMissingFromStatementCount { get; set; }

    /// <summary>Ledger rows that have not been reconciled at all yet.</summary>
    [JsonPropertyName("unreconciledTransactionCount")]
    public int UnreconciledTransactionCount { get; set; }

    /// <summary>Previous imports, newest first (so the business can see its import history).</summary>
    [JsonPropertyName("imports")]
    public List<MpesaStatementImportResponse> Imports { get; set; } = new();
}
