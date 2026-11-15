using System.Text.Json.Serialization;

namespace PunchedApi.Application.DTOs;

/// <summary>One point on a time series (a day, week or month bucket).</summary>
public class FinancialTrendPoint
{
    /// <summary>Bucket start, ISO-8601 (<c>2026-11-08</c> for a day, <c>2026-11</c> for a month).</summary>
    [JsonPropertyName("period")]
    public string Period { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("amount")]
    public decimal Amount { get; set; }

    [JsonPropertyName("count")]
    public int Count { get; set; }
}

/// <summary>Revenue analytics: trends and the breakdowns behind them.</summary>
public class RevenueAnalytics
{
    /// <summary><c>day</c> | <c>week</c> | <c>month</c> — the bucket size the period implied.</summary>
    [JsonPropertyName("granularity")]
    public string Granularity { get; set; } = "day";

    [JsonPropertyName("overTime")]
    public List<FinancialTrendPoint> OverTime { get; set; } = new();

    [JsonPropertyName("bySource")]
    public List<FinancialBreakdownItem> BySource { get; set; } = new();

    [JsonPropertyName("byPaymentMethod")]
    public List<FinancialBreakdownItem> ByPaymentMethod { get; set; } = new();

    /// <summary>Revenue attributed to each service (from the appointment's service lines).</summary>
    [JsonPropertyName("byService")]
    public List<FinancialBreakdownItem> ByService { get; set; } = new();

    /// <summary>Revenue attributed to each service category.</summary>
    [JsonPropertyName("byCategory")]
    public List<FinancialBreakdownItem> ByCategory { get; set; } = new();

    /// <summary>Revenue attributed to each staff member (the appointment's assigned professional).</summary>
    [JsonPropertyName("byStaff")]
    public List<FinancialBreakdownItem> ByStaff { get; set; } = new();

    [JsonPropertyName("averageTransactionValue")]
    public decimal AverageTransactionValue { get; set; }
}

/// <summary>Payment analytics: how money arrives, and what state it is in.</summary>
public class PaymentAnalytics
{
    [JsonPropertyName("byMethod")]
    public List<FinancialBreakdownItem> ByMethod { get; set; } = new();

    [JsonPropertyName("verifiedAmount")]
    public decimal VerifiedAmount { get; set; }

    [JsonPropertyName("verifiedCount")]
    public int VerifiedCount { get; set; }

    [JsonPropertyName("pendingAmount")]
    public decimal PendingAmount { get; set; }

    [JsonPropertyName("pendingCount")]
    public int PendingCount { get; set; }

    [JsonPropertyName("rejectedAmount")]
    public decimal RejectedAmount { get; set; }

    [JsonPropertyName("rejectedCount")]
    public int RejectedCount { get; set; }

    /// <summary>Refunds recorded in the period, as a trend.</summary>
    [JsonPropertyName("refundsOverTime")]
    public List<FinancialTrendPoint> RefundsOverTime { get; set; } = new();

    [JsonPropertyName("refundTotal")]
    public decimal RefundTotal { get; set; }

    [JsonPropertyName("refundCount")]
    public int RefundCount { get; set; }
}

/// <summary>Expense analytics: where the money goes, and income against it.</summary>
public class ExpenseAnalytics
{
    [JsonPropertyName("overTime")]
    public List<FinancialTrendPoint> OverTime { get; set; } = new();

    [JsonPropertyName("byCategory")]
    public List<FinancialBreakdownItem> ByCategory { get; set; } = new();

    [JsonPropertyName("total")]
    public decimal Total { get; set; }

    [JsonPropertyName("count")]
    public int Count { get; set; }
}

/// <summary>Business performance for the period, in the plainest terms available.</summary>
public class FinancialPerformance
{
    [JsonPropertyName("totalIncome")]
    public decimal TotalIncome { get; set; }

    [JsonPropertyName("totalExpenses")]
    public decimal TotalExpenses { get; set; }

    [JsonPropertyName("totalRefunds")]
    public decimal TotalRefunds { get; set; }

    /// <summary>Income − expenses − refunds. The period's operating result.</summary>
    [JsonPropertyName("netOperatingResult")]
    public decimal NetOperatingResult { get; set; }

    [JsonPropertyName("outstandingBalances")]
    public decimal OutstandingBalances { get; set; }

    [JsonPropertyName("outstandingAppointmentCount")]
    public int OutstandingAppointmentCount { get; set; }

    /// <summary>Refunds as a share of income, rounded to 1dp (0 when there was no income).</summary>
    [JsonPropertyName("refundRate")]
    public decimal RefundRate { get; set; }

    [JsonPropertyName("averageTransactionValue")]
    public decimal AverageTransactionValue { get; set; }

    /// <summary>Income minus expenses, as a share of income, rounded to 1dp.</summary>
    [JsonPropertyName("marginPercentage")]
    public decimal MarginPercentage { get; set; }
}

/// <summary>The financial analytics payload: revenue, payments, expenses and performance for one period.</summary>
public class FinancialAnalyticsResponse
{
    [JsonPropertyName("period")]
    public string Period { get; set; } = string.Empty;

    [JsonPropertyName("from")]
    public DateTime From { get; set; }

    [JsonPropertyName("to")]
    public DateTime To { get; set; }

    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "KES";

    [JsonPropertyName("revenue")]
    public RevenueAnalytics Revenue { get; set; } = new();

    [JsonPropertyName("payments")]
    public PaymentAnalytics Payments { get; set; } = new();

    [JsonPropertyName("expenses")]
    public ExpenseAnalytics Expenses { get; set; } = new();

    [JsonPropertyName("performance")]
    public FinancialPerformance Performance { get; set; } = new();
}

/// <summary>A predefined expense category, as the UI needs it.</summary>
public class ExpenseCategoryResponse
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;
}
