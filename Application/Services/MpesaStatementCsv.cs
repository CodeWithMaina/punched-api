using System.Globalization;

namespace PunchedApi.Application.Services;

/// <summary>One parsed statement line, before it becomes a database row.</summary>
public sealed record ParsedStatementLine(
    string ReceiptNumber,
    decimal Amount,
    DateTime? TransactionAt,
    string? PayerName,
    string? PayerPhone,
    string? AccountReference,
    string? Narrative);

/// <summary>What a statement file yielded, plus anything the importer should be told.</summary>
public sealed record ParsedStatement(List<ParsedStatementLine> Lines, string? Notes);

/// <summary>
/// Parses an M-Pesa statement exported as CSV (the file a business downloads from the M-Pesa portal,
/// or the equivalent export its bank provides).
///
/// <para><b>Why a tolerant parser.</b> Statement exports vary: the column order shifts, headers are
/// worded differently ("Receipt No" / "Transaction ID" / "M-PESA Receipt"), and a details column may
/// be quoted and contain commas. A rigid parser would reject real files, so this one recognises known
/// header names, falls back to a documented positional order, and reports how many rows it could not
/// read instead of silently dropping them.</para>
///
/// <para><b>Nothing is invented.</b> A row without a receipt code and an amount is skipped and counted.
/// </para>
/// </summary>
public static class MpesaStatementCsv
{
    /// <summary>Maximum rows accepted in one import, so a huge file cannot exhaust the request.</summary>
    public const int MaxLines = 5000;

    private static readonly string[] ReceiptKeys =
        { "receipt", "receiptno", "receiptnumber", "transactionid", "mpesareceipt" };

    private static readonly string[] AmountKeys = { "amount", "paidin", "credit", "value", "transactionamount" };
    private static readonly string[] DateKeys = { "date", "completiontime", "transactiondate", "time" };
    private static readonly string[] NameKeys = { "name", "payer", "payername", "customer" };
    private static readonly string[] PhoneKeys = { "msisdn", "phone", "phonenumber", "mobile" };
    private static readonly string[] AccountKeys = { "account", "accountnumber", "accountreference", "billrefnumber", "orderid" };
    private static readonly string[] NarrativeKeys = { "details", "narrative", "description", "transactiontype", "reason" };

    /// <summary>Used when a file carries no header row: receipt, date, amount, name, phone, account, details.</summary>
    private static readonly ColumnMap DefaultMap = new(0, 2, 1, 3, 4, 5, 6);

    public static ParsedStatement Parse(string content)
    {
        var rows = SplitRows(content);

        if (rows.Count == 0)
            return new ParsedStatement(new List<ParsedStatementLine>(), "The file was empty.");

        // A header is present when the first row's amount column holds no number.
        var header = LooksLikeHeader(rows[0]) ? rows[0] : null;
        var body = header == null ? rows : rows.Skip(1).ToList();
        var map = header == null ? DefaultMap : BuildMap(header);

        var lines = new List<ParsedStatementLine>();
        var skipped = 0;

        foreach (var row in body.Take(MaxLines))
        {
            if (row.All(string.IsNullOrWhiteSpace)) continue;

            var receipt = Cell(row, map.Receipt)?.Trim().ToUpperInvariant();
            var amountText = Cell(row, map.Amount);

            if (string.IsNullOrWhiteSpace(receipt) || !TryAmount(amountText, out var amount) || amount <= 0m)
            {
                skipped++;
                continue;
            }

            lines.Add(new ParsedStatementLine(
                ReceiptNumber: receipt!,
                Amount: amount,
                TransactionAt: ParseDate(Cell(row, map.Date)),
                PayerName: Clean(Cell(row, map.Name), 200),
                PayerPhone: Clean(Cell(row, map.Phone), 40),
                AccountReference: Clean(Cell(row, map.Account), 100),
                Narrative: Clean(Cell(row, map.Narrative), 500)));
        }

        if (body.Count > MaxLines)
            return new ParsedStatement(lines,
                $"Read the first {MaxLines} rows; the file contains more. Import the remainder as a separate statement.");

        return new ParsedStatement(lines,
            skipped > 0 ? $"{skipped} row(s) had no readable receipt code or amount and were skipped." : null);
    }

    private readonly record struct ColumnMap(int Receipt, int Amount, int Date, int Name, int Phone, int Account, int Narrative);

    private static bool LooksLikeHeader(IReadOnlyList<string> row) =>
        row.Any(cell => cell != null && AmountKeys.Any(k => Normalise(cell).Contains(k)));

    private static ColumnMap BuildMap(IReadOnlyList<string> header)
    {
        int Find(string[] keys, int fallback)
        {
            for (var i = 0; i < header.Count; i++)
            {
                var normalised = Normalise(header[i]);
                if (normalised.Length == 0) continue;
                if (keys.Any(key => normalised.Contains(key))) return i;
            }

            return fallback;
        }

        return new ColumnMap(
            Receipt: Find(ReceiptKeys, 0),
            Amount: Find(AmountKeys, 2),
            Date: Find(DateKeys, 1),
            Name: Find(NameKeys, 3),
            Phone: Find(PhoneKeys, 4),
            Account: Find(AccountKeys, 5),
            Narrative: Find(NarrativeKeys, 6));
    }

    /// <summary>
    /// Splits CSV text into rows of cells, honouring double quotes (including escaped quotes and
    /// embedded newlines), which is how a details column containing a comma is written.
    /// </summary>
    private static List<List<string>> SplitRows(string content)
    {
        var rows = new List<List<string>>();
        var cells = new List<string>();
        var cell = new System.Text.StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < content.Length && content[i + 1] == '"')
                    {
                        cell.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    cell.Append(c);
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inQuotes = true;
                    break;
                case ',':
                case ';':
                    cells.Add(cell.ToString());
                    cell.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    cells.Add(cell.ToString());
                    cell.Clear();
                    rows.Add(cells);
                    cells = new List<string>();
                    break;
                default:
                    cell.Append(c);
                    break;
            }
        }

        if (cell.Length > 0 || cells.Count > 0)
        {
            cells.Add(cell.ToString());
            rows.Add(cells);
        }

        return rows;
    }

    private static string? Cell(IReadOnlyList<string> row, int index) =>
        index >= 0 && index < row.Count ? row[index] : null;

    private static string? Clean(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }

    private static string Normalise(string? value) =>
        value == null ? string.Empty : new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static bool TryAmount(string? raw, out decimal amount)
    {
        amount = 0m;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        // Statements render amounts as "1,000.00", "1000", "KES 1,000.00" or "(500)" for a reversal.
        var cleaned = raw.Trim().Trim('"');
        var negative = cleaned.StartsWith('(') && cleaned.EndsWith(')');

        cleaned = new string(cleaned.Where(c => char.IsDigit(c) || c == '.' || c == '-').ToArray());
        if (cleaned.Length == 0) return false;

        if (!decimal.TryParse(cleaned, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out amount))
            return false;

        if (negative && amount > 0m) amount = -amount;

        return true;
    }

    /// <summary>
    /// Parses the date shapes statements use: <c>2026-11-08 14:03:22</c>, <c>08/11/2026 14:03</c> (day
    /// first, which is how Safaricom writes it), <c>8-Nov-2026</c> and an ISO instant. A date that
    /// cannot be read stays null rather than being guessed.
    /// </summary>
    private static DateTime? ParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var value = raw.Trim().Trim('"');

        string[] formats =
        {
            "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd",
            "dd/MM/yyyy HH:mm:ss", "dd/MM/yyyy HH:mm", "dd/MM/yyyy",
            "d/M/yyyy HH:mm:ss", "d/M/yyyy HH:mm", "d/M/yyyy",
            "dd-MM-yyyy HH:mm:ss", "dd-MM-yyyy",
            "d-MMM-yyyy HH:mm", "d-MMM-yyyy", "dd MMM yyyy HH:mm"
        };

        if (DateTime.TryParseExact(value, formats, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out var parsed))
            return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var offset))
            return offset.UtcDateTime;

        return null;
    }
}
