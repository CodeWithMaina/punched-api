using System.Text.Json.Serialization;

namespace PunchedApi.Application.DTOs;

// ═══════════════════════════════════════════════════════════════
//  STOREFRONT ENQUIRY DTOs
//  An anonymous visitor's question, sent from a business's public
//  page. The platform records it as a notification for the owner;
//  it does not attempt to reply, and it stores no visitor identity.
// ═══════════════════════════════════════════════════════════════

/// <summary>
/// A storefront enquiry.
///
/// <see cref="Topic"/> and <see cref="Answers"/> are the visitor's own words,
/// kept short and bounded — they are rendered into a notification payload, so
/// they must not be able to carry arbitrary content.
/// </summary>
public class CreateStorefrontEnquiryRequest
{
    /// <summary>One of <c>services</c>, <c>event</c>, <c>booking</c>, <c>other</c>.</summary>
    [JsonPropertyName("topic")]
    public string Topic { get; set; } = "other";

    /// <summary>The visitor's answers, in the order the form asked them.</summary>
    [JsonPropertyName("answers")]
    public List<string> Answers { get; set; } = new();

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Normalised to <c>254…</c> by the caller; validated here.</summary>
    [JsonPropertyName("phoneNumber")]
    public string PhoneNumber { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

/// <summary>
/// What the visitor is told afterwards: a reference to quote and the business's
/// name. Nothing here claims the business has replied or will reply by a given
/// time — only that the enquiry was received.
/// </summary>
public class StorefrontEnquiryResponse
{
    /// <summary>Short, human-readable reference derived from the notification row.</summary>
    [JsonPropertyName("reference")]
    public string Reference { get; set; } = string.Empty;

    [JsonPropertyName("businessName")]
    public string BusinessName { get; set; } = string.Empty;

    [JsonPropertyName("receivedAt")]
    public DateTime ReceivedAt { get; set; }
}
