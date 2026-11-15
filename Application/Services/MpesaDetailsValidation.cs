using System.Text.RegularExpressions;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Application.Services;

/// <summary>
/// The single definition of what a valid manual M-PESA configuration looks like.
///
/// <para>The owner API validates with this before it saves, and the owner UI mirrors
/// the exact same rules (<c>lib/payments/validation.ts</c>), so a value the form
/// accepts is a value the server accepts — the rules are stated once per side and
/// deliberately kept in lock-step. Field keys are the request's JSON names so a
/// validation failure can be placed directly under the field that caused it.</para>
/// </summary>
public static partial class MpesaDetailsValidation
{
    public const int PayeeNameMaxLength = 60;
    public const int InstructionsMaxLength = 300;

    [GeneratedRegex(@"^\d{5,7}$")]
    private static partial Regex ShortNumberRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,20}$")]
    private static partial Regex AccountRegex();

    [GeneratedRegex(@"^254[71]\d{8}$")]
    private static partial Regex NormalisedPhoneRegex();

    // ── Field keys (request JSON names) ─────────────────────

    public const string PaybillNumberField = "mpesaPaybillNumber";
    public const string PaybillAccountField = "mpesaPaybillAccount";
    public const string TillNumberField = "mpesaTillNumber";
    public const string PhoneNumberField = "mpesaPhoneNumber";
    public const string PayeeNameField = "mpesaPayeeName";
    public const string InstructionsField = "mpesaInstructions";

    // ── Individual rules ────────────────────────────────────

    /// <summary>PayBill number: digits only, 5–7 digits. Returns an error, or null when valid/blank.</summary>
    public static string? ValidatePaybillNumber(string? value)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v)) return null;
        return ShortNumberRegex().IsMatch(v) ? null : "Use 5–7 digits.";
    }

    /// <summary>PayBill account: 1–20 characters of letters, digits, <c>-</c> or <c>_</c>.</summary>
    public static string? ValidatePaybillAccount(string? value)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v)) return null;
        return AccountRegex().IsMatch(v) ? null : "Use 1–20 letters, digits, - or _.";
    }

    /// <summary>Till number: digits only, 5–7 digits. Returns an error, or null when valid/blank.</summary>
    public static string? ValidateTillNumber(string? value)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v)) return null;
        return ShortNumberRegex().IsMatch(v) ? null : "Use 5–7 digits.";
    }

    /// <summary>Payee name: optional, trimmed, max 60 characters.</summary>
    public static string? ValidatePayeeName(string? value)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v)) return null;
        return v.Length <= PayeeNameMaxLength ? null : $"Use {PayeeNameMaxLength} characters or fewer.";
    }

    /// <summary>Instructions: optional free text, max 300 characters.</summary>
    public static string? ValidateInstructions(string? value)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v)) return null;
        return v.Length <= InstructionsMaxLength ? null : $"Use {InstructionsMaxLength} characters or fewer.";
    }

    /// <summary>
    /// Accepts <c>07XXXXXXXX</c>, <c>01XXXXXXXX</c>, <c>+2547XXXXXXXX</c>,
    /// <c>2547XXXXXXXX</c>, <c>7XXXXXXXX</c> (and the <c>1…</c> equivalents), tolerating
    /// spaces and dashes. Returns the normalised <c>254XXXXXXXXX</c> form, or null when
    /// the value is blank or not a valid Kenyan mobile number.
    /// </summary>
    public static string? NormalisePhone(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var digits = new string(value.Where(char.IsDigit).ToArray());

        string? candidate = digits.Length switch
        {
            9 when digits[0] is '7' or '1' => "254" + digits,
            10 when digits.StartsWith("07") || digits.StartsWith("01") => "254" + digits[1..],
            12 when digits.StartsWith("254") => digits,
            _ => null
        };

        return candidate is not null && NormalisedPhoneRegex().IsMatch(candidate) ? candidate : null;
    }

    /// <summary>Valid when blank (optional) or a valid Kenyan mobile number in any accepted form.</summary>
    public static string? ValidatePhone(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return NormalisePhone(value) is not null ? null : "Enter a valid M-PESA number, e.g. 0712 440 001.";
    }

    // ── Whole-configuration validation ──────────────────────

    /// <summary>
    /// Validates the editable configuration. Blank optional fields are fine; any
    /// supplied field must be well-formed. When manual M-PESA is being enabled the
    /// selected method's own fields must be complete, so a business can never be saved
    /// switched-on but unpayable.
    /// </summary>
    public static Dictionary<string, string> Validate(SavePaymentConfigRequest request)
    {
        var errors = new Dictionary<string, string>();

        void Add(string field, string? message)
        {
            if (message is not null) errors[field] = message;
        }

        // Format rules apply to whatever was supplied, so bad data cannot be stored
        // for the inactive method either.
        Add(PaybillNumberField, ValidatePaybillNumber(request.MpesaPaybillNumber));
        Add(PaybillAccountField, ValidatePaybillAccount(request.MpesaPaybillAccount));
        Add(TillNumberField, ValidateTillNumber(request.MpesaTillNumber));
        Add(PhoneNumberField, ValidatePhone(request.MpesaPhoneNumber));
        Add(PayeeNameField, ValidatePayeeName(request.MpesaPayeeName));
        Add(InstructionsField, ValidateInstructions(request.MpesaInstructions));

        if (!request.ManualMpesaEnabled) return errors;

        var method = ManualMpesaMethods.Normalise(request.MpesaAccountType);
        switch (method)
        {
            case ManualMpesaMethods.SendMoney:
                if (NormalisePhone(request.MpesaPhoneNumber) is null)
                    errors[PhoneNumberField] = "Enter the M-PESA number customers should send to.";
                break;

            case ManualMpesaMethods.Till:
                if (string.IsNullOrWhiteSpace(request.MpesaTillNumber)
                    || ValidateTillNumber(request.MpesaTillNumber) is not null)
                    errors[TillNumberField] = "Enter the till number customers should pay.";
                break;

            default: // paybill
                if (string.IsNullOrWhiteSpace(request.MpesaPaybillNumber)
                    || ValidatePaybillNumber(request.MpesaPaybillNumber) is not null)
                    errors[PaybillNumberField] = "Enter the business number customers should pay.";
                if (string.IsNullOrWhiteSpace(request.MpesaPaybillAccount)
                    || ValidatePaybillAccount(request.MpesaPaybillAccount) is not null)
                    errors[PaybillAccountField] = "Enter the account number customers should use.";
                break;
        }

        return errors;
    }
}
