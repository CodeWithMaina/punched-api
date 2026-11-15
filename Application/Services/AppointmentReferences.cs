namespace PunchedApi.Application.Services;

/// <summary>
/// The human-facing appointment reference, and how a search term is turned into a
/// predicate the database can actually execute.
///
/// <para><b>The format.</b> <c>AP-</c> followed by the first 8 hex digits of the
/// appointment's id — the same string the dashboard prints next to a booking, so a
/// customer reading a reference off a screen can be found by it. It is derived, never
/// stored: there is exactly one representation of an appointment's identity, and it
/// cannot drift from the row it describes.</para>
///
/// <para><b>Why this file exists.</b> A uuid cannot be matched by prefix in EF Core
/// (there is no translatable "cast to text"), so a reference search is expressed as an
/// id <i>range</i>. Postgres orders <c>uuid</c> by its bytes, and EF's SQLite provider
/// stores a guid in canonical text form ordered lexicographically — both agree that the
/// ids whose canonical form starts with <c>XXXXXXXX</c> are exactly the ids in
/// <c>[XXXXXXXX0000…, (XXXXXXXX+1)0000…)</c>. One comparison, one index seek, and no
/// schema change.</para>
/// </summary>
public static class AppointmentReferences
{
    /// <summary>The printable prefix.</summary>
    public const string Prefix = "AP-";

    /// <summary>How many hex digits of the id the reference carries.</summary>
    private const int ReferenceHexLength = 8;

    /// <summary>Shortest prefix accepted from a user (a 2-digit prefix would match too much).</summary>
    private const int MinimumPrefixLength = 4;

    /// <summary>An inclusive/exclusive id window that contains exactly one reference prefix.</summary>
    public readonly record struct PrefixRange(Guid Low, Guid High);

    /// <summary>The reference for an appointment id (`AP-3F2A9C41`). Mirrors the web client exactly.</summary>
    public static string For(Guid id) =>
        Prefix + id.ToString("N")[..ReferenceHexLength].ToUpperInvariant();

    /// <summary>
    /// A full guid, with or without the <c>AP-</c> prefix and with or without dashes.
    /// Null when the term is not an id at all.
    /// </summary>
    public static Guid? TryParseId(string? value)
    {
        var raw = Strip(value);
        if (raw.Length == 0 || raw.Length > 32) return null;
        if (raw.Length == 32 && Guid.TryParseExact(raw, "N", out var parsed)) return parsed;
        return Guid.TryParse(raw, out var dashed) ? dashed : null;
    }

    /// <summary>
    /// The id window for a reference prefix such as <c>AP-3F2A</c>. False when the term
    /// is not a usable prefix (too short, not hex, or the one overflow case where the
    /// successor prefix would carry past the last uuid).
    /// </summary>
    public static bool TryParsePrefixRange(string? value, out PrefixRange range)
    {
        range = default;

        var raw = Strip(value);
        if (raw.Length < MinimumPrefixLength || raw.Length > ReferenceHexLength) return false;

        foreach (var c in raw)
            if (!Uri.IsHexDigit(c)) return false;

        if (!uint.TryParse(raw, System.Globalization.NumberStyles.HexNumber, null, out var prefix))
            return false;

        // The next prefix in the id space. The shift matters: `3F2A` means "every id whose
        // first four hex digits are 3F2A", so its successor is 3F2B, NOT 3F2A0001 — the
        // padding below is only for the zeros that must follow.
        var shift = 4 * (ReferenceHexLength - raw.Length);
        var next = (long)prefix + 1;
        if (next << shift > uint.MaxValue) return false;  // FFFFFFFF: past the end of the id space

        var low = ToCanonical(raw.PadRight(ReferenceHexLength, '0'));
        var high = ToCanonical(((uint)(next << shift)).ToString("X8"));
        if (low == null || high == null) return false;

        range = new PrefixRange(low.Value, high.Value);
        return true;
    }

    /// <summary>Drops the <c>AP-</c> marker and every separator so `<c>ap 3f2a-9c41</c>` reads as hex.</summary>
    private static string Strip(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var trimmed = value.Trim();
        if (trimmed.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[Prefix.Length..];

        var buffer = new char[trimmed.Length];
        var length = 0;
        foreach (var c in trimmed)
        {
            if (c is '-' or ' ' or '_') continue;
            buffer[length++] = c;
        }

        return new string(buffer, 0, length);
    }

    /// <summary>`prefix` hex digits followed by zeros, as a guid.</summary>
    private static Guid? ToCanonical(string prefixHex, int digits = 8) =>
        Guid.TryParseExact(CanonicalHex(prefixHex, digits), "N", out var id) ? id : null;

    private static Guid? ToCanonical(uint prefix, int digits) =>
        ToCanonical(prefix.ToString("X8"), digits);

    /// <summary>Pads/truncates a hex prefix to 8 digits and pads the rest of the 32-char id with zeros.</summary>
    private static string CanonicalHex(string prefixHex, int digits)
    {
        var head = prefixHex.Length >= digits ? prefixHex[..digits] : prefixHex.PadRight(digits, '0');
        return head.PadRight(32, '0');
    }
}
