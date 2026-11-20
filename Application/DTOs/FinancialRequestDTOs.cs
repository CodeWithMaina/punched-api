using System.Text.Json.Serialization;

namespace PunchedApi.Application.DTOs;

/// <summary>
/// Record business income that did not originate from a Punched appointment: a walk-in,
/// a direct M-Pesa Till/Paybill/Send Money payment, a product sale, cash, a bank transfer,
/// or an "other" business receipt.
///
/// <para><b>Owner-recorded vs M-Pesa evidence.</b> <see cref="ExternalReference"/> is what
/// the owner typed (an M-Pesa code, a bank reference); the server treats it as a claim, not
/// as proof. A row recorded with an M-Pesa method therefore starts
/// <c>pending</c> until an owner verifies it, while cash/bank/card rows are recorded as
/// verified by the operator who entered them.</para>
/// </summary>
public class RecordIncomeRequest
{
    /// <summary>Amount received, in major units (KES). Required and must be positive.</summary>
    [JsonPropertyName("amount")]
    public decimal Amount { get; set; }

    /// <summary>Where the income came from: <c>walk_in</c> | <c>product_sale</c> | <c>manual_income</c> | <c>other</c>.</summary>
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    /// <summary>How it was paid: <c>mpesa_till</c> | <c>mpesa_paybill</c> | <c>mpesa_send_money</c> | <c>cash</c> | <c>bank_transfer</c> | <c>card</c> | <c>other</c>.</summary>
    [JsonPropertyName("paymentMethod")]
    public string? PaymentMethod { get; set; }

    /// <summary>Optional M-Pesa code, bank reference or receipt number the customer was given.</summary>
    [JsonPropertyName("transactionReference")]
    public string? TransactionReference { get; set; }

    /// <summary>Optional existing customer this money belongs to.</summary>
    [JsonPropertyName("customerId")]
    public Guid? CustomerId { get; set; }

    /// <summary>
    /// Optional name of whoever paid, when they are not a registered customer (a walk-in, a
    /// product sale). Stored on the row and matched by the register's customer/payee search.
    /// </summary>
    [JsonPropertyName("customerName")]
    public string? CustomerName { get; set; }

    /// <summary>Optional free-text description ("Haircut + beard trim").</summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>Optional expense-style category for product sales; ignored unless a known key.</summary>
    [JsonPropertyName("category")]
    public string? Category { get; set; }

    /// <summary>When the money changed hands. Defaults to now.</summary>
    [JsonPropertyName("transactionDate")]
    public DateTime? TransactionDate { get; set; }

    /// <summary>ISO-4217 currency. KES only in the MVP.</summary>
    [JsonPropertyName("currency")]
    public string? Currency { get; set; }
}

/// <summary>A payment the business spent money on.</summary>
public class CreateExpenseRequest
{
    [JsonPropertyName("amount")]
    public decimal Amount { get; set; }

    /// <summary>One of the predefined category keys (see <c>GET /v1/finances/expense-categories</c>).</summary>
    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("paymentMethod")]
    public string? PaymentMethod { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>Optional supplier/payee the money was paid to; searchable on the register.</summary>
    [JsonPropertyName("payee")]
    public string? Payee { get; set; }

    /// <summary>Optional receipt/bank reference.</summary>
    [JsonPropertyName("transactionReference")]
    public string? TransactionReference { get; set; }

    [JsonPropertyName("transactionDate")]
    public DateTime? TransactionDate { get; set; }

    [JsonPropertyName("currency")]
    public string? Currency { get; set; }
}

/// <summary>An edit to an existing expense. Every changed field is audited.</summary>
public class UpdateExpenseRequest
{
    [JsonPropertyName("amount")]
    public decimal? Amount { get; set; }

    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("paymentMethod")]
    public string? PaymentMethod { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("transactionReference")]
    public string? TransactionReference { get; set; }

    /// <summary>Optional supplier/payee, when the entry is corrected.</summary>
    [JsonPropertyName("payee")]
    public string? Payee { get; set; }

    [JsonPropertyName("transactionDate")]
    public DateTime? TransactionDate { get; set; }

    /// <summary>Why the amount or details changed — recorded in the audit trail.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}


/// <summary>
/// A refund against an earlier income row. The original transaction is never modified: the
/// refund is its own ledger row linked to it, so "what came in" and "what went back" stay
/// separately provable.
/// </summary>
public class RecordRefundRequest
{
    /// <summary>The income transaction being refunded (from the ledger or a payment).</summary>
    [JsonPropertyName("transactionId")]
    public Guid? TransactionId { get; set; }

    /// <summary>Alternatively, refund the recognised income of this payment.</summary>
    [JsonPropertyName("paymentId")]
    public Guid? PaymentId { get; set; }

    /// <summary>
    /// Optional client-supplied key that makes the refund safe to retry. A double-tapped
    /// "Refund" button, a network retry or a resent request all carry the same key, and the
    /// second and later attempts return the FIRST refund instead of recording another one —
    /// without it, two identical submissions would each pass the "is there anything left to
    /// refund?" check and refund twice.
    /// </summary>
    [JsonPropertyName("idempotencyKey")]
    public string? IdempotencyKey { get; set; }

    /// <summary>How much goes back. Must not exceed what is left refundable.</summary>
    [JsonPropertyName("amount")]
    public decimal Amount { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    /// <summary>How the money went back: cash, M-Pesa, bank transfer, …</summary>
    [JsonPropertyName("refundMethod")]
    public string? RefundMethod { get; set; }

    /// <summary>The refund's own reference (M-Pesa code, bank reference).</summary>
    [JsonPropertyName("refundReference")]
    public string? RefundReference { get; set; }

    [JsonPropertyName("refundDate")]
    public DateTime? RefundDate { get; set; }

    /// <summary>
    /// Whether the money has actually left the business yet. A refund is recorded as
    /// <c>pending</c> unless this is true, so requesting a refund never overstates what has
    /// been paid back.
    /// </summary>
    [JsonPropertyName("completed")]
    public bool Completed { get; set; }
}

/// <summary>Owner decision on a recorded claim (a manual M-Pesa income row, or a flagged one).</summary>
public class VerifyFinancialTransactionRequest
{
    [JsonPropertyName("note")]
    public string? Note { get; set; }

    /// <summary>Corrected amount when the claim was for the wrong figure. Optional.</summary>
    [JsonPropertyName("amount")]
    public decimal? Amount { get; set; }
}

/// <summary>Refusing a recorded claim. The reason is required: the trail must say why.</summary>
public class RejectFinancialTransactionRequest
{
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}

/// <summary>Reconciliation decision on a transaction (<c>matched</c>, <c>discrepancy</c>, <c>excluded</c>, <c>unreconciled</c>).</summary>
public class ReconcileTransactionRequest
{
    /// <summary><c>matched</c> | <c>discrepancy</c> | <c>excluded</c> | <c>unreconciled</c>.</summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }

    /// <summary>The statement line to match against (optional when the status is not <c>matched</c>).</summary>
    [JsonPropertyName("statementLineId")]
    public Guid? StatementLineId { get; set; }
}

/// <summary>
/// Import an M-Pesa statement. The content is the statement itself (CSV), never a link to
/// Safaricom — there is no direct integration, so the business supplies the file it
/// downloaded from its own M-Pesa account.
/// </summary>
public class ImportMpesaStatementRequest
{
    [JsonPropertyName("fileName")]
    public string? FileName { get; set; }

    /// <summary>CSV text. A header row is expected but not required.</summary>
    [JsonPropertyName("content")]
    public string? Content { get; set; }

    /// <summary>Re-import a file whose content was already imported (only with an explicit override).</summary>
    [JsonPropertyName("allowDuplicate")]
    public bool AllowDuplicate { get; set; }
}

/// <summary>Match an imported statement line to a Punched transaction (or exclude it).</summary>
public class MatchStatementLineRequest
{
    [JsonPropertyName("transactionId")]
    public Guid? TransactionId { get; set; }

    [JsonPropertyName("paymentId")]
    public Guid? PaymentId { get; set; }

    /// <summary><c>matched</c> | <c>discrepancy</c> | <c>excluded</c>.</summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }
}

/// <summary>Voiding a financial row. The row stays visible; it stops counting.</summary>
public class VoidFinancialRecordRequest
{
    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}
