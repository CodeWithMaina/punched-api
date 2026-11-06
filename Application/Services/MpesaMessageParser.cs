using System.Globalization;
using System.Text.RegularExpressions;

namespace PunchedApi.Application.Services;

/// <summary>What a pasted M-PESA confirmation message yielded, or why it was refused.</summary>
public sealed record MpesaMessageParseResult(
    bool IsValid,
    string? TransactionCode,
    decimal? Amount,
    DateTime? TransactionAtUtc,
    string? Recipient,
    string? Payer,
    string? FailureCode,
    string? FailureMessage)
{
    public static MpesaMessageParseResult Ok(
        string transactionCode, decimal amount, DateTime? transactionAtUtc, string? recipient, string? payer) =>
        new(true, transactionCode, amount, transactionAtUtc, recipient, payer, null, null);

    public static MpesaMessageParseResult Invalid(string code, string message) =>
        new(false, null, null, null, null, null, code, message);
}

/// <summary>
/// Parses a Safaricom M-PESA confirmation SMS.
///
/// <para><b>Why a parser and not a regex in the controller.</b> The message is
/// customer-supplied text, so it is the one input in the payment flow an attacker
/// fully controls. Centralising it means one place decides what "looks like a
/// genuine M-PESA confirmation" and what a receipt code looks like, so every
/// caller — storefront, dashboard, future channels — behaves identically.</para>
///
/// <para><b>What this deliberately does NOT do.</b> It never marks a payment paid.
/// A parser can only produce evidence; the business owner is the authority. Nor
/// does it trust any amount the client sent: the amount returned here comes from
/// the message text, and the caller compares <i>that</i> against what is owed.</para>
///
/// <para><b>Shape recognised.</b> Safaricom's confirmation carries a receipt code,
/// an amount in KES, optionally a date/time, optionally a payee and payer. The
/// parser therefore requires the two fields a confirmation cannot lack — a receipt
/// code and a KES amount — plus a transaction verb and the product name, and treats
/// everything else as optional. Text that merely contains a code-shaped word and a
/// number but reads like unrelated prose is refused.</para>
/// </summary>
public static partial class MpesaMessageParser
{
    /// <summary>Longest message we will look at. Beyond this it is not a confirmation SMS.</summary>
    public const int MaxMessageLength = 1000;

    /// <summary>
    /// M-PESA receipt codes are 10 alphanumeric characters (e.g. <c>QJK42NUM01</c>).
    /// Anchored on word boundaries so a code embedded in a longer token is not
    /// mistaken for one.
    /// </summary>
    [GeneratedRegex(@"\b([A-Z0-9]{10})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ReceiptCode();

    /// <summary>A KES amount. Accepts `Ksh1,000`, `Ksh 1,000.50`, `KES 250`, `Kshs.500`.</summary>
    [GeneratedRegex(@"\b(?:Kshs?\.?|KES)\s*([0-9][0-9,]*(?:\.[0-9]{1,2})?)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex AmountWithCurrency();

    /// <summary>Words that make the text a transaction statement rather than unrelated prose.</summary>
    [GeneratedRegex(@"\b(?:sent|paid|pay|received|bought|withdrawn|transferred|deposited)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex TransactionVerb();

    /// <summary>The product name, which any genuine confirmation carries.</summary>
    [GeneratedRegex(@"M[\s-]?PESA", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex MpesaBrand();

    /// <summary>`on 12/5/26 at 3:04 PM` — Safaricom's date shape (day/month/2-digit year).</summary>
    [GeneratedRegex(@"\bon\s+(\d{1,2})/(\d{1,2})/(\d{2,4})\s+at\s+(\d{1,2}):(\d{2})\s*(AM|PM)?", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex TransactionDate();

    /// <summary>`to JOHN DOE for account 12345` — the payee clause.</summary>
    [GeneratedRegex(@"\bto\s+([A-Z0-9][A-Z0-9 .&'\-]{1,60}?)\s+(?:for account|for|on|\.|,|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex Recipient();

    /// <summary>`from JOHN DOE` — the payer clause.</summary>
    [GeneratedRegex(@"\bfrom\s+([A-Z0-9][A-Z0-9 .&'\-]{1,60}?)\s+(?:on|\.|,|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex Payer();

    /// <summary>
    /// Parse a pasted confirmation message.
    ///
    /// <para>Ordering matters: the cheap structural refusals (empty, too long,
    /// unrelated prose) run before any field extraction, so a hostile blob never
    /// reaches a regex that could be made to work.</para>
    /// </summary>
    public static MpesaMessageParseResult Parse(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return MpesaMessageParseResult.Invalid(
                "MPESA_MESSAGE_EMPTY", "Paste the M-PESA confirmation message you received.");

        // Collapse whitespace runs (including the newlines an SMS is pasted with) so
        // one set of patterns handles both a single line and a wrapped message.
        var text = Regex.Replace(message, @"\s+", " ").Trim();

        if (text.Length > MaxMessageLength)
            return MpesaMessageParseResult.Invalid(
                "MPESA_MESSAGE_TOO_LONG", "That message is too long to be an M-PESA confirmation.");

        if (!MpesaBrand().IsMatch(text) || !TransactionVerb().IsMatch(text))
            return MpesaMessageParseResult.Invalid(
                "MPESA_MESSAGE_NOT_CONFIRMATION",
                "That does not look like an M-PESA confirmation message. Copy the whole SMS you received.");

        var code = FindReceiptCode(text);
        if (code == null)
            return MpesaMessageParseResult.Invalid(
                "MPESA_MESSAGE_NO_CODE",
                "We could not find an M-PESA transaction code in that message.");

        var amountMatch = AmountWithCurrency().Match(text);
        if (!amountMatch.Success || !TryParseAmount(amountMatch.Groups[1].Value, out var amount) || amount <= 0)
            return MpesaMessageParseResult.Invalid(
                "MPESA_MESSAGE_NO_AMOUNT",
                "We could not find the amount in that message.");

        return MpesaMessageParseResult.Ok(
            code,
            amount,
            ParseTransactionDate(text),
            CleanClause(Recipient().Match(text)),
            CleanClause(Payer().Match(text)));
    }

    /// <summary>
    /// Find the receipt code. Safaricom writes it bare. Bare 10-character tokens are
    /// accepted only when they carry at least one letter — an all-digit token of that
    /// length is a phone number or an account, never a receipt.
    /// </summary>
    private static string? FindReceiptCode(string text)
    {
        foreach (Match candidate in ReceiptCode().Matches(text))
        {
            var value = candidate.Groups[1].Value;

            if (!value.Any(char.IsLetter)) continue;
            // Reject the currency keyword itself.
            if (value.Equals("KES", StringComparison.OrdinalIgnoreCase)) continue;
            // Reject a code that is glued to the currency word (`Ksh1,000.00KES0001`),
            // which is how a hand-fabricated message usually looks. `\b` already
            // guarantees a word boundary, so this only needs to look at the prefix.
            var at = candidate.Index;
            if (at >= 2 && (text[at - 1] == 'h' || text[at - 1] == 'H')) continue;

            return value.ToUpperInvariant();
        }

        return null;
    }

    /// <summary>`1,000.50` → <c>1000.50</c>. Invariant culture, so a comma is always a thousands separator.</summary>
    private static bool TryParseAmount(string raw, out decimal amount) =>
        decimal.TryParse(
            raw.Replace(",", string.Empty, StringComparison.Ordinal),
            NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture,
            out amount);

    /// <summary>
    /// `on 12/5/26 at 3:04 PM` → a UTC instant. Interpreted as East Africa Time
    /// (UTC+3), which is the zone Safaricom stamps its confirmations in — treating it
    /// as UTC would shift every transaction three hours into the past.
    /// </summary>
    private static DateTime? ParseTransactionDate(string text)
    {
        var match = TransactionDate().Match(text);
        if (!match.Success) return null;

        if (!int.TryParse(match.Groups[1].Value, out var day) ||
            !int.TryParse(match.Groups[2].Value, out var month) ||
            !int.TryParse(match.Groups[3].Value, out var year) ||
            !int.TryParse(match.Groups[4].Value, out var hour) ||
            !int.TryParse(match.Groups[5].Value, out var minute))
            return null;

        if (year < 100) year += 2000;

        var meridiem = match.Groups[6].Success ? match.Groups[6].Value.ToUpperInvariant() : null;
        if (meridiem == "PM" && hour < 12) hour += 12;
        if (meridiem == "AM" && hour == 12) hour = 0;

        if (month is < 1 or > 12 || day < 1 || day > 31 || hour > 23 || minute > 59)
            return null;

        try
        {
            var eat = new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.FromHours(3));
            return eat.UtcDateTime;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>Tidy a captured name clause, or null when nothing usable was captured.</summary>
    private static string? CleanClause(Match match)
    {
        if (!match.Success) return null;

        var value = match.Groups[1].Value.Trim().Trim('.', ',');
        if (value.Length == 0) return null;
        // A clause that is only digits, or a bare receipt code, is not a name.
        if (value.All(char.IsDigit)) return null;
        if (value.Length == 10 && !value.Contains(' ')) return null;

        return value.Length > 200 ? value[..200] : value;
    }
}
