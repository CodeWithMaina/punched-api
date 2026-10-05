using System.Text.Json.Serialization;

namespace PunchedApi.Application.DTOs;

// ── Requests ─────────────────────────────────────────────

/// <summary>Create a payment for an appointment. Amount is computed server-side — NEVER sent by the client.</summary>
public class CreatePaymentRequest
{
    [JsonPropertyName("appointmentId")]
    public Guid AppointmentId { get; set; }

    /// <summary>"cash" or "mpesa".</summary>
    [JsonPropertyName("method")]
    public string Method { get; set; } = "cash";

    /// <summary>"full_payment" | "booking_fee" | "balance_payment". Defaults to full_payment.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "full_payment";

    /// <summary>M-PESA phone (07XX / 2547XX). Required for method=mpesa.</summary>
    [JsonPropertyName("phoneNumber")]
    public string? PhoneNumber { get; set; }

    /// <summary>Optional idempotency key (client-generated).</summary>
    [JsonPropertyName("idempotencyKey")]
    public string? IdempotencyKey { get; set; }
}

public class ConfirmCashRequest
{
    /// <summary>Required audit note explaining the confirmation (who took the money, etc.).</summary>
    [JsonPropertyName("note")]
    public string? Note { get; set; }
}

public class ReversePaymentRequest
{
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;
}

public class SavePaymentConfigRequest
{
    [JsonPropertyName("cashEnabled")]
    public bool CashEnabled { get; set; } = true;

    [JsonPropertyName("mpesaEnabled")]
    public bool MpesaEnabled { get; set; }

    /// <summary>"paybill" or "till".</summary>
    [JsonPropertyName("mpesaAccountType")]
    public string MpesaAccountType { get; set; } = "paybill";

    [JsonPropertyName("mpesaShortCode")]
    public string? MpesaShortCode { get; set; }

    /// <summary>Daraja consumer key. Null/omitted keeps the existing value.</summary>
    [JsonPropertyName("consumerKey")]
    public string? ConsumerKey { get; set; }

    [JsonPropertyName("consumerSecret")]
    public string? ConsumerSecret { get; set; }

    /// <summary>STK passkey for the shortcode. Null/omitted keeps the existing value.</summary>
    [JsonPropertyName("passkey")]
    public string? Passkey { get; set; }
}

public class PaymentListQuery
{
    public Guid? AppointmentId { get; set; }
    public Guid? CustomerId { get; set; }
    public string? Status { get; set; }
    public string? Method { get; set; }
    public string? Type { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
}
