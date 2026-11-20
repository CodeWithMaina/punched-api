using System.Text.Json.Serialization;

namespace PunchedApi.Application.DTOs;

/// <summary>
/// Operational DTOs for the payment → ledger outbox. These are platform-admin shapes, not business
/// finance shapes: they carry identifiers, timings and failure codes for diagnosis, and deliberately
/// exclude M-Pesa messages, phone numbers and customer names.
/// </summary>
public class AdminLedgerObligationResponse
{
    [JsonPropertyName("id")] public Guid Id { get; set; }
    [JsonPropertyName("paymentId")] public Guid PaymentId { get; set; }
    [JsonPropertyName("businessId")] public Guid BusinessId { get; set; }
    [JsonPropertyName("paymentReference")] public string? PaymentReference { get; set; }
    [JsonPropertyName("paymentAmount")] public decimal PaymentAmount { get; set; }
    [JsonPropertyName("paymentStatus")] public string PaymentStatus { get; set; } = string.Empty;
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
    [JsonPropertyName("attempts")] public int Attempts { get; set; }
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; }
    [JsonPropertyName("lastAttemptAt")] public DateTime? LastAttemptAt { get; set; }
    [JsonPropertyName("nextAttemptAt")] public DateTime NextAttemptAt { get; set; }
    [JsonPropertyName("completedAt")] public DateTime? CompletedAt { get; set; }
    [JsonPropertyName("lockedAt")] public DateTime? LockedAt { get; set; }
    [JsonPropertyName("financialTransactionId")] public Guid? FinancialTransactionId { get; set; }
    [JsonPropertyName("failureKind")] public string? FailureKind { get; set; }
    [JsonPropertyName("lastErrorCode")] public string? LastErrorCode { get; set; }
    [JsonPropertyName("lastError")] public string? LastError { get; set; }
    [JsonPropertyName("ageMinutes")] public int AgeMinutes { get; set; }
}

public class AdminLedgerObligationListResponse
{
    [JsonPropertyName("items")] public List<AdminLedgerObligationResponse> Items { get; set; } = new();
    [JsonPropertyName("page")] public int Page { get; set; }
    [JsonPropertyName("pageSize")] public int PageSize { get; set; }
    [JsonPropertyName("totalCount")] public int TotalCount { get; set; }
}

public class AdminLedgerSummaryResponse
{
    [JsonPropertyName("pending")] public int Pending { get; set; }
    [JsonPropertyName("processing")] public int Processing { get; set; }
    [JsonPropertyName("failed")] public int Failed { get; set; }
    [JsonPropertyName("completed")] public int Completed { get; set; }
    [JsonPropertyName("settledPaymentsMissingIncome")] public int SettledPaymentsMissingIncome { get; set; }
    [JsonPropertyName("obligationsWithoutIncome")] public int ObligationsWithoutIncome { get; set; }
    [JsonPropertyName("oldestPendingAgeMinutes")] public int OldestPendingAgeMinutes { get; set; }
}

public class AdminLedgerAnomalyResponse
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = string.Empty;
    [JsonPropertyName("paymentId")] public Guid? PaymentId { get; set; }
    [JsonPropertyName("reference")] public string? Reference { get; set; }
    [JsonPropertyName("detail")] public string? Detail { get; set; }
}

public class AdminLedgerHealthResponse
{
    /// <summary>Null when the request was not scoped to one business.</summary>
    [JsonPropertyName("businessId")] public Guid? BusinessId { get; set; }
    [JsonPropertyName("summary")] public AdminLedgerSummaryResponse Summary { get; set; } = new();
    [JsonPropertyName("settledPaymentsWithoutIncome")] public int SettledPaymentsWithoutIncome { get; set; }
    [JsonPropertyName("ledgerRowsForUnsettledPayments")] public int LedgerRowsForUnsettledPayments { get; set; }
    [JsonPropertyName("duplicateIncomeGroups")] public int DuplicateIncomeGroups { get; set; }
    [JsonPropertyName("orphanedIncomeRows")] public int OrphanedIncomeRows { get; set; }
    [JsonPropertyName("samples")] public List<AdminLedgerAnomalyResponse> Samples { get; set; } = new();
    [JsonPropertyName("generatedAt")] public DateTime GeneratedAt { get; set; }
}

public class AdminLedgerReconciliationResponse
{
    [JsonPropertyName("obligationsCreated")] public int ObligationsCreated { get; set; }
    [JsonPropertyName("obligationsCompleted")] public int ObligationsCompleted { get; set; }
    [JsonPropertyName("obligationsRequeued")] public int ObligationsRequeued { get; set; }
    [JsonPropertyName("settledPaymentsWithoutIncome")] public int SettledPaymentsWithoutIncome { get; set; }
    [JsonPropertyName("duplicateIncomeGroups")] public int DuplicateIncomeGroups { get; set; }
    [JsonPropertyName("orphanedIncomeRows")] public int OrphanedIncomeRows { get; set; }
    [JsonPropertyName("failedObligations")] public int FailedObligations { get; set; }
    [JsonPropertyName("startedAt")] public DateTime StartedAt { get; set; }
    [JsonPropertyName("finishedAt")] public DateTime FinishedAt { get; set; }
}

public class AdminLedgerRetryResponse
{
    [JsonPropertyName("requeued")] public bool Requeued { get; set; }
    [JsonPropertyName("outcome")] public string Outcome { get; set; } = string.Empty;
    [JsonPropertyName("financialTransactionId")] public Guid? FinancialTransactionId { get; set; }
    [JsonPropertyName("errorCode")] public string? ErrorCode { get; set; }
    [JsonPropertyName("obligation")] public AdminLedgerObligationResponse? Obligation { get; set; }
}
