using System.Text.Json;
using PunchedApi.Application.Notifications;

namespace PunchedApi.Application.Services;

/// <summary>
/// Helpers shared by the admin and business notification management services.
///
/// Everything here is deliberately deterministic and dependency-free so the
/// masking and classification rules are testable in isolation and identical
/// wherever a notification record is projected to a DTO.
/// </summary>
internal static class NotificationManagementShared
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Keys that must never be projected into an API response.</summary>
    private static readonly HashSet<string> SensitiveKeys =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "password", "pin", "cvv", "cardnumber", "refreshtoken", "secret", "apikey",
            "code", "verificationcode", "otp", "token"
        };

    /// <summary>Human labels for the code-defined template tokens.</summary>
    private static readonly IReadOnlyDictionary<string, string> VariableLabels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["businessName"] = "Business name",
            ["customerName"] = "Customer name",
            ["customerEmail"] = "Customer email",
            ["customerPhone"] = "Customer phone",
            ["scheduledAt"] = "Appointment date & time",
            ["appointmentDate"] = "Appointment date",
            ["appointmentTime"] = "Appointment time",
            ["serviceName"] = "Service name",
            ["staffName"] = "Staff name",
            ["reference"] = "Booking reference",
            ["amount"] = "Payment amount",
            ["reason"] = "Reason",
            ["rewardName"] = "Reward name",
            ["stamps"] = "Stamp count",
            ["balance"] = "Loyalty balance",
            ["daysInactive"] = "Days inactive",
            ["renewalDate"] = "Renewal date",
            ["topic"] = "Enquiry topic",
            ["visitorName"] = "Visitor name",
            ["BookLink"] = "Booking link",
            ["BillingLink"] = "Billing link",
            ["NotebookLink"] = "Rewards link",
        };

    /// <summary>Realistic sample values so previews render believably.</summary>
    private static readonly IReadOnlyDictionary<string, string> VariableSamples =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["businessName"] = "Aurelia Luxe Hair Atelier",
            ["customerName"] = "Amina Odhiambo",
            ["customerEmail"] = "amina.odhiambo@example.com",
            ["customerPhone"] = "+254712345678",
            ["scheduledAt"] = "Fri 14 Mar 2026, 10:30",
            ["appointmentDate"] = "14 Mar 2026",
            ["appointmentTime"] = "10:30",
            ["serviceName"] = "Balayage & Gloss",
            ["staffName"] = "Njeri Kamau",
            ["reference"] = "BK-20260314-0042",
            ["amount"] = "KES 6,500",
            ["reason"] = "Card declined",
            ["rewardName"] = "Free deep-conditioning treatment",
            ["stamps"] = "7",
            ["balance"] = "7 stamps",
            ["daysInactive"] = "45",
            ["renewalDate"] = "31 Mar 2026",
            ["topic"] = "Bridal party pricing",
            ["visitorName"] = "Brian Mwangi",
            ["BookLink"] = "https://punched.app/book",
            ["BillingLink"] = "https://punched.app/billing",
            ["NotebookLink"] = "https://punched.app/rewards",
        };

    /// <summary>Masks an email address: <c>am***@example.com</c>.</summary>
    public static string MaskEmail(string email)
    {
        var at = email.IndexOf('@');
        if (at <= 0) return "***";
        var local = email[..at];
        var domain = email[at..];
        var keep = Math.Min(2, Math.Max(1, local.Length - 1));
        return $"{local[..keep]}***{domain}";
    }

    /// <summary>Masks a phone number, keeping the last three digits.</summary>
    public static string MaskPhone(string phone)
    {
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        if (digits.Length <= 3) return "***";
        return $"***{digits[^3..]}";
    }

    /// <summary>
    /// Masks whatever recipient identity is available. Falls back to the user id
    /// prefix so a record is still identifiable without leaking a contact.
    /// </summary>
    public static string MaskRecipient(string? email, string? phone, Guid userId)
    {
        if (!string.IsNullOrWhiteSpace(email)) return MaskEmail(email!);
        if (!string.IsNullOrWhiteSpace(phone)) return MaskPhone(phone!);
        return $"user:{userId.ToString("N")[..8]}";
    }

    /// <summary>Short, non-reversible error summary for list views.</summary>
    public static string? SummarizeError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return null;
        var trimmed = error.Trim();
        return trimmed.Length <= 160 ? trimmed : trimmed[..160];
    }

    /// <summary>
    /// Classification used by the failures page: permanent vs transient vs
    /// retry-exhausted, derived from the ledger's error vocabulary.
    /// </summary>
    public static string Classify(string status, string? error)
    {
        if (string.Equals(status, "sent", StringComparison.OrdinalIgnoreCase)) return "delivered";
        if (string.Equals(status, "pending", StringComparison.OrdinalIgnoreCase)) return "pending";
        if (string.Equals(status, "processing", StringComparison.OrdinalIgnoreCase)) return "processing";
        if (string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase)) return "cancelled";

        if (string.IsNullOrWhiteSpace(error)) return "unknown";
        if (error.StartsWith("permanent", StringComparison.OrdinalIgnoreCase)) return "permanent_failure";
        if (error.StartsWith("retry_exhausted", StringComparison.OrdinalIgnoreCase)) return "retry_exhausted";
        return "transient_failure";
    }

    /// <summary>A failed row may be retried only when it is retry-exhausted.</summary>
    public static bool IsRetryEligible(string status, string? error) =>
        string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
        && error is not null
        && error.StartsWith("retry_exhausted:", StringComparison.OrdinalIgnoreCase);

    /// <summary>A pending (not yet claimed) row may be cancelled.</summary>
    public static bool IsCancellable(string status) =>
        string.Equals(status, "pending", StringComparison.OrdinalIgnoreCase);

    /// <summary>Serializes a payload with sensitive keys removed.</summary>
    public static string SanitizePayload(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return "{}";

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return "{}";

            var sanitized = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (SensitiveKeys.Contains(property.Name)) continue;
                sanitized[property.Name] = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()
                    : property.Value.Clone();
            }

            return JsonSerializer.Serialize(sanitized, JsonOptions);
        }
        catch (JsonException)
        {
            // A malformed payload must not fail the read; report emptiness instead.
            return "{}";
        }
    }

    /// <summary>True when the payload marks the intent as a test send.</summary>
    public static bool IsTestPayload(string? payloadJson) =>
        !string.IsNullOrWhiteSpace(payloadJson)
        && payloadJson!.Contains("\"__test\":true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads the delivery attempts recorded on a ledger row.</summary>
    public static List<DTOs.AdminNotificationAttemptDto> ReadAttempts(string? attemptsJson)
    {
        if (string.IsNullOrWhiteSpace(attemptsJson)) return [];

        try
        {
            using var document = JsonDocument.Parse(attemptsJson);
            if (!document.RootElement.TryGetProperty("attempts", out var attempts)
                || attempts.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var result = new List<DTOs.AdminNotificationAttemptDto>();
            foreach (var attempt in attempts.EnumerateArray())
            {
                result.Add(new DTOs.AdminNotificationAttemptDto
                {
                    StartedAtUtc = ReadDate(attempt, "startedAtUtc"),
                    CompletedAtUtc = ReadDate(attempt, "completedAtUtc"),
                    Outcome = ReadString(attempt, "outcome") ?? "unknown",
                });
            }

            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static DateTime ReadDate(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            return default;
        return DateTime.TryParse(value.GetString(), out var parsed) ? parsed.ToUniversalTime() : default;
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Every token a business may use for a registered type.</summary>
    public static List<string> AvailableTokens(NotificationTypeDef definition)
    {
        var tokens = new List<string>();
        foreach (var key in definition.PayloadSchema.Required) tokens.Add(key);
        foreach (var key in definition.PayloadSchema.Optional) tokens.Add(key);
        tokens.AddRange(["BookLink", "BillingLink", "NotebookLink"]);
        return tokens.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t, StringComparer.Ordinal).ToList();
    }

    /// <summary>Describes one token for the editor's variable panel.</summary>
    public static DTOs.BusinessNotificationVariableDto DescribeToken(string token) => new()
    {
        Token = token,
        Label = VariableLabels.TryGetValue(token, out var label) ? label : Humanize(token),
        SampleValue = VariableSamples.TryGetValue(token, out var sample) ? sample : $"<{Humanize(token)}>",
    };

    /// <summary>Builds a realistic sample payload for previews and test sends.</summary>
    public static Dictionary<string, object?> SamplePayload(NotificationTypeDef definition)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var token in AvailableTokens(definition))
        {
            payload[token] = VariableSamples.TryGetValue(token, out var sample) ? sample : string.Empty;
        }

        // The registry's own required keys win, so a preview always satisfies the
        // payload contract the delivery pipeline enforces.
        foreach (var required in definition.PayloadSchema.Required)
        {
            if (!payload.ContainsKey(required)) payload[required] = string.Empty;
        }

        return payload;
    }

    /// <summary>Estimates SMS segments (GSM-7 vs UCS-2, concatenation aware).</summary>
    public static (int Characters, int Segments, string Encoding) EstimateSmsSegments(string body)
    {
        var text = body ?? string.Empty;
        var isGsm = IsGsm7(text);
        var length = text.Length;
        var (single, multi) = isGsm ? (160, 153) : (70, 67);
        var segments = length == 0 ? 0 : length <= single ? 1 : (int)Math.Ceiling(length / (double)multi);
        return (length, segments, isGsm ? "GSM-7" : "UCS-2");
    }

    private static bool IsGsm7(string text)
    {
        const string gsm =
            "@£$¥èéùìòÇ\nØø\rÅåΔ_ΦΓΛΩΠΨΣΘΞÆæßÉ !\"#¤%&'()*+,-./0123456789:;<=>?" +
            "¡ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÑÜ§¿abcdefghijklmnopqrstuvwxyzäöñüà";
        foreach (var character in text)
        {
            if (!gsm.Contains(character) && character != '\u20AC' && character != '\u00A0')
                return false;
        }

        return true;
    }

    /// <summary>Turns a camelCase token into a readable label.</summary>
    public static string Humanize(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var builder = new System.Text.StringBuilder(value.Length + 4);
        for (var i = 0; i < value.Length; i++)
        {
            var character = value[i];
            if (i > 0 && char.IsUpper(character) && !char.IsUpper(value[i - 1])) builder.Append(' ');
            builder.Append(i == 0 ? char.ToUpperInvariant(character) : character);
        }

        return builder.ToString();
    }

    /// <summary>Percent with one decimal, or null when there is nothing to divide.</summary>
    public static double? Percent(long numerator, long denominator) =>
        denominator <= 0 ? null : Math.Round(numerator * 100.0 / denominator, 1);

    /// <summary>
    /// A channel is "available" when the deployment registered a delivery
    /// implementation for it. In-app always has one (the inbox insert).
    /// </summary>
    public static bool IsChannelAvailable(string channel, IReadOnlySet<string> registeredChannels) =>
        channel == NotificationChannel.InApp || registeredChannels.Contains(channel);
}
