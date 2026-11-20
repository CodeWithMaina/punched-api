namespace PunchedApi.Domain.Entities;

/// <summary>
/// Wire (snake_case) names for the financial enums — the ONE place the HTTP contract is
/// translated to and from the C# enums, so a client echoing back a value it was given is
/// never rejected and the documented contract cannot drift from the enum.
/// </summary>
public static class FinancialWire
{
    // ── Transaction type ────────────────────────────────────
    public static string ToWire(FinancialTransactionType value) => value switch
    {
        FinancialTransactionType.Income => "income",
        FinancialTransactionType.Expense => "expense",
        FinancialTransactionType.Refund => "refund",
        _ => "adjustment"
    };

    public static bool TryParseType(string? value, out FinancialTransactionType type)
    {
        type = default;
        switch (Normalise(value))
        {
            case "income": type = FinancialTransactionType.Income; return true;
            case "expense": type = FinancialTransactionType.Expense; return true;
            case "refund": type = FinancialTransactionType.Refund; return true;
            case "adjustment": type = FinancialTransactionType.Adjustment; return true;
            default: return false;
        }
    }

    // ── Transaction source ──────────────────────────────────
    public static string ToWire(TransactionSource value) => value switch
    {
        TransactionSource.PunchedBooking => "punched_booking",
        TransactionSource.WalkIn => "walk_in",
        TransactionSource.ProductSale => "product_sale",
        TransactionSource.ManualIncome => "manual_income",
        _ => "other"
    };

    public static bool TryParseSource(string? value, out TransactionSource source)
    {
        source = default;
        switch (Normalise(value))
        {
            case "punchedbooking": source = TransactionSource.PunchedBooking; return true;
            case "walkin": source = TransactionSource.WalkIn; return true;
            case "productsale": source = TransactionSource.ProductSale; return true;
            case "manualincome": source = TransactionSource.ManualIncome; return true;
            case "other": source = TransactionSource.Other; return true;
            default: return false;
        }
    }

    // ── Payment method ──────────────────────────────────────
    public static string ToWire(FinancialPaymentMethod value) => value switch
    {
        FinancialPaymentMethod.MpesaTill => "mpesa_till",
        FinancialPaymentMethod.MpesaPaybill => "mpesa_paybill",
        FinancialPaymentMethod.MpesaSendMoney => "mpesa_send_money",
        FinancialPaymentMethod.Cash => "cash",
        FinancialPaymentMethod.BankTransfer => "bank_transfer",
        FinancialPaymentMethod.Card => "card",
        _ => "other"
    };

    public static bool TryParseMethod(string? value, out FinancialPaymentMethod method)
    {
        method = default;
        switch (Normalise(value))
        {
            case "mpesatill": method = FinancialPaymentMethod.MpesaTill; return true;
            case "mpesapaybill": method = FinancialPaymentMethod.MpesaPaybill; return true;
            case "mpesasendmoney": method = FinancialPaymentMethod.MpesaSendMoney; return true;
            case "cash": method = FinancialPaymentMethod.Cash; return true;
            case "banktransfer": method = FinancialPaymentMethod.BankTransfer; return true;
            case "card": method = FinancialPaymentMethod.Card; return true;
            case "other": method = FinancialPaymentMethod.Other; return true;
            default: return false;
        }
    }

    /// <summary>True for the three M-Pesa routes (which need an M-Pesa reference to be credible).</summary>
    public static bool IsMpesa(FinancialPaymentMethod method) =>
        method is FinancialPaymentMethod.MpesaTill
            or FinancialPaymentMethod.MpesaPaybill
            or FinancialPaymentMethod.MpesaSendMoney;

    /// <summary>
    /// The short label a person reads ("M-Pesa", "Cash", "Bank"). Every M-Pesa route folds into
    /// one label on purpose: a breakdown by channel would be four bars saying M-Pesa.
    /// </summary>
    public static string MethodLabel(FinancialPaymentMethod value) => MethodGroup(value).Label;

    /// <summary>
    /// The grouping a breakdown panel uses: a stable key plus the label that goes with it, so the
    /// three M-Pesa routes total into ONE row and a chart cannot show two bars called "M-Pesa".
    /// </summary>
    public static (string Key, string Label) MethodGroup(FinancialPaymentMethod value) => value switch
    {
        FinancialPaymentMethod.MpesaTill => ("mpesa", "M-Pesa"),
        FinancialPaymentMethod.MpesaPaybill => ("mpesa", "M-Pesa"),
        FinancialPaymentMethod.MpesaSendMoney => ("mpesa", "M-Pesa"),
        FinancialPaymentMethod.Cash => ("cash", "Cash"),
        FinancialPaymentMethod.BankTransfer => ("bank", "Bank"),
        FinancialPaymentMethod.Card => ("card", "Card"),
        _ => ("other", "Other")
    };

    // ── Verification status ─────────────────────────────────
    public static string ToWire(FinancialVerificationStatus value) => value switch
    {
        FinancialVerificationStatus.Verified => "verified",
        FinancialVerificationStatus.Rejected => "rejected",
        _ => "pending"
    };

    public static bool TryParseVerification(string? value, out FinancialVerificationStatus status)
    {
        status = default;
        switch (Normalise(value))
        {
            case "pending": status = FinancialVerificationStatus.Pending; return true;
            case "verified": status = FinancialVerificationStatus.Verified; return true;
            case "rejected": status = FinancialVerificationStatus.Rejected; return true;
            default: return false;
        }
    }

    // ── Direction ───────────────────────────────────────────
    /// <summary>
    /// The one place direction is decided. Income and adjustments bring money in; expenses and
    /// refunds take it (or send it back) out.
    /// </summary>
    public static FinancialDirection DirectionOf(FinancialTransactionType type) =>
        type is FinancialTransactionType.Expense or FinancialTransactionType.Refund
            ? FinancialDirection.Outgoing
            : FinancialDirection.Incoming;

    public static string ToWire(FinancialDirection value) =>
        value == FinancialDirection.Outgoing ? "outgoing" : "incoming";

    /// <summary>
    /// Reads a direction the way a client actually sends it: the wire name
    /// (<c>incoming</c>/<c>outgoing</c>), the terse short form (<c>in</c>/<c>out</c>) or the
    /// column heading (<c>money_in</c>/<c>money_out</c>) all resolve.
    /// </summary>
    public static bool TryParseDirection(string? value, out FinancialDirection direction)
    {
        direction = FinancialDirection.Incoming;

        switch (Normalise(value))
        {
            case "incoming":
            case "in":
            case "moneyin":
            case "credit":
                direction = FinancialDirection.Incoming;
                return true;
            case "outgoing":
            case "out":
            case "moneyout":
            case "debit":
                direction = FinancialDirection.Outgoing;
                return true;
            default:
                return false;
        }
    }

    // ── Reconciliation status ───────────────────────────────
    public static string ToWire(ReconciliationStatus value) => value switch
    {
        ReconciliationStatus.Matched => "matched",
        ReconciliationStatus.Discrepancy => "discrepancy",
        ReconciliationStatus.Excluded => "excluded",
        _ => "unreconciled"
    };

    public static bool TryParseReconciliation(string? value, out ReconciliationStatus status)
    {
        status = default;
        switch (Normalise(value))
        {
            case "unreconciled": status = ReconciliationStatus.Unreconciled; return true;
            case "matched": status = ReconciliationStatus.Matched; return true;
            case "discrepancy": status = ReconciliationStatus.Discrepancy; return true;
            case "excluded": status = ReconciliationStatus.Excluded; return true;
            default: return false;
        }
    }

    /// <summary>Lower-cases and drops separators so any spelling of a name compares equal.</summary>
    private static string Normalise(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var trimmed = value.Trim();
        var buffer = new char[trimmed.Length];
        var length = 0;
        foreach (var c in trimmed)
        {
            if (c is '_' or '-' or ' ') continue;
            buffer[length++] = char.ToLowerInvariant(c);
        }
        return new string(buffer, 0, length);
    }
}
