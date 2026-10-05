using PunchedApi.Domain.Entities;

namespace PunchedApi.Application.Services;

/// <summary>
/// Wire-format names for <see cref="PaymentType"/> — the ONE place the
/// snake_case contract (<c>full_payment</c> | <c>booking_fee</c> | <c>balance_payment</c>,
/// as documented on <c>CreatePaymentRequest.Type</c>) is translated to and from the
/// C# enum.
///
/// Why this exists: <c>Enum.TryParse</c> is case-insensitive but NOT
/// separator-insensitive, so parsing the documented <c>"full_payment"</c> against the
/// member <c>FullPayment</c> silently fails. Parsing must therefore normalise
/// separators explicitly, and responses must emit the same snake_case the request
/// side documents — otherwise a client can echo back its own value and be rejected.
/// </summary>
public static class PaymentTypes
{
    /// <summary>Full payment for the appointment's services.</summary>
    public const string FullPayment = "full_payment";

    /// <summary>Upfront booking fee (partial payment on booking).</summary>
    public const string BookingFee = "booking_fee";

    /// <summary>Payment against an outstanding balance.</summary>
    public const string BalancePayment = "balance_payment";

    /// <summary>
    /// Parses a wire value into <see cref="PaymentType"/>. Accepts the canonical
    /// snake_case form plus tolerant variants (<c>fullpayment</c>, <c>FullPayment</c>,
    /// <c>full-payment</c>, <c>full payment</c>) so hand-written clients and older
    /// callers keep working. Unknown or blank values return false — never a default,
    /// because defaulting a money-affecting type silently would be wrong.
    /// </summary>
    public static bool TryParse(string? value, out PaymentType type)
    {
        type = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        switch (Normalize(value))
        {
            case "fullpayment":
                type = PaymentType.FullPayment;
                return true;
            case "bookingfee":
                type = PaymentType.BookingFee;
                return true;
            case "balancepayment":
                type = PaymentType.BalancePayment;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Canonical snake_case wire value for a parsed type.</summary>
    public static string ToWire(PaymentType type) => type switch
    {
        PaymentType.BookingFee => BookingFee,
        PaymentType.BalancePayment => BalancePayment,
        _ => FullPayment
    };

    /// <summary>Lower-cases and drops separators so any spelling of a name compares equal.</summary>
    private static string Normalize(string value)
    {
        var buffer = new char[value.Length];
        var length = 0;
        foreach (var c in value)
        {
            if (c is '_' or '-' or ' ')
                continue;
            buffer[length++] = char.ToLowerInvariant(c);
        }
        return new string(buffer, 0, length);
    }
}
