using System.Text;
using System.Text.Json;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Application.Attendance.Verification;

/// <summary>
/// Shared helpers for the verification namespace: wire-value conversion
/// (SCREAMING_SNAKE, plan §6.6), required-method parsing, and summary building.
/// </summary>
public static class AttendanceVerification
{
    private static readonly JsonSerializerOptions SummaryOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Wire value of an enum member: SCREAMING_SNAKE of the member name
    /// (<c>AuthenticatedUser</c> → "AUTHENTICATED_USER", <c>Standard</c> → "STANDARD").
    /// </summary>
    public static string WireValue<TEnum>(TEnum value) where TEnum : struct, Enum
    {
        var name = value.ToString();
        var sb = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
                sb.Append('_');
            sb.Append(char.ToUpperInvariant(name[i]));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Parses a SCREAMING_SNAKE wire value back to the enum (plan §6.6 — the
    /// API contract must not shift if C# names are refactored).
    /// </summary>
    public static AttendanceVerificationMethod? ParseWireValue(string? wire)
    {
        if (string.IsNullOrWhiteSpace(wire)) return null;
        foreach (AttendanceVerificationMethod method in Enum.GetValues<AttendanceVerificationMethod>())
        {
            if (string.Equals(WireValue(method), wire, StringComparison.OrdinalIgnoreCase))
                return method;
        }
        return null;
    }

    /// <summary>Parses a SCREAMING_SNAKE attendance mode ("STANDARD" → <c>Standard</c>).</summary>
    public static AttendanceMode? ParseModeWireValue(string? wire)
    {
        if (string.IsNullOrWhiteSpace(wire)) return null;
        foreach (AttendanceMode mode in Enum.GetValues<AttendanceMode>())
        {
            if (string.Equals(WireValue(mode), wire, StringComparison.OrdinalIgnoreCase))
                return mode;
        }
        return null;
    }

    /// <summary>
    /// Parses the policy's required-verifications JSON (the
    /// <c>Module.DependenciesJson</c> precedent: plain string array via
    /// <c>System.Text.Json</c>, no jsonb). Unknown values are skipped; the
    /// engine then fails closed (<c>VERIFICATION_METHOD_UNAVAILABLE</c>) for
    /// anything no registered verifier implements.
    /// </summary>
    public static List<AttendanceVerificationMethod> ParseRequiredMethods(string? requiredVerificationsJson)
    {
        var methods = new List<AttendanceVerificationMethod>();
        if (string.IsNullOrWhiteSpace(requiredVerificationsJson)) return methods;
        try
        {
            var wires = JsonSerializer.Deserialize<string[]>(requiredVerificationsJson);
            if (wires == null) return methods;
            foreach (var wire in wires)
            {
                var method = ParseWireValue(wire);
                if (method.HasValue && !methods.Contains(method.Value))
                    methods.Add(method.Value);
            }
        }
        catch (JsonException)
        {
            // Malformed policy JSON ⇒ empty set ⇒ engine fails closed.
        }
        return methods;
    }

    /// <summary>Serialises a verifier summary fragment.</summary>
    public static string BuildSummary(string type, bool passed, DateTime checkedAt) =>
        JsonSerializer.Serialize(new { type, passed, checkedAt }, SummaryOptions);
}