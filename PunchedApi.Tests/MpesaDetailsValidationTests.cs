using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;

namespace PunchedApi.Tests;

/// <summary>
/// The shared M-PESA validation rules. Every accepted phone format and every
/// rejection case is pinned here, because these rules gate where a customer's
/// money is sent — a wrong rule is not a cosmetic bug.
/// </summary>
public class MpesaDetailsValidationTests
{
    // ── PayBill number ──────────────────────────────────────

    [Theory]
    [InlineData("12345")]
    [InlineData("247247")]
    [InlineData("1234567")]
    [InlineData(" 247247 ")]
    public void PaybillNumber_Accepts_5_to_7_digits(string value)
        => Assert.Null(MpesaDetailsValidation.ValidatePaybillNumber(value));

    [Theory]
    [InlineData("1234")]
    [InlineData("12345678")]
    [InlineData("24724a")]
    public void PaybillNumber_Rejects_badValues(string value)
        => Assert.NotNull(MpesaDetailsValidation.ValidatePaybillNumber(value));

    [Fact]
    public void PaybillNumber_Blank_isOptional()
        => Assert.Null(MpesaDetailsValidation.ValidatePaybillNumber(null));

    // ── PayBill account ─────────────────────────────────────

    [Theory]
    [InlineData("12345")]
    [InlineData("AURELIA-1042")]
    [InlineData("ab_12")]
    [InlineData("A")]
    public void PaybillAccount_Accepts_letters_digits_dash_underscore(string value)
        => Assert.Null(MpesaDetailsValidation.ValidatePaybillAccount(value));

    [Theory]
    [InlineData("has space")]
    [InlineData("bad!char")]
    [InlineData("123456789012345678901")]
    public void PaybillAccount_Rejects_badValues(string value)
        => Assert.NotNull(MpesaDetailsValidation.ValidatePaybillAccount(value));

    // ── Till number ─────────────────────────────────────────

    [Theory]
    [InlineData("12345")]
    [InlineData("556677")]
    [InlineData("5123456")]
    public void TillNumber_Accepts_5_to_7_digits(string value)
        => Assert.Null(MpesaDetailsValidation.ValidateTillNumber(value));

    [Theory]
    [InlineData("1234")]
    [InlineData("12345678")]
    [InlineData("51234a")]
    public void TillNumber_Rejects_badValues(string value)
        => Assert.NotNull(MpesaDetailsValidation.ValidateTillNumber(value));

    // ── Send Money phone ────────────────────────────────────

    [Theory]
    [InlineData("0712440001", "254712440001")]
    [InlineData("0112440001", "254112440001")]
    [InlineData("+254712440001", "254712440001")]
    [InlineData("254712440001", "254712440001")]
    [InlineData("712440001", "254712440001")]
    [InlineData("+2547 12 440 001", "254712440001")]
    [InlineData("0712-440-001", "254712440001")]
    [InlineData("712-440-001", "254712440001")]
    [InlineData("112440001", "254112440001")]
    [InlineData(" 0712 440 001 ", "254712440001")]
    public void Phone_AcceptsEveryForm_andNormalises(string input, string expected)
    {
        Assert.Null(MpesaDetailsValidation.ValidatePhone(input));
        Assert.Equal(expected, MpesaDetailsValidation.NormalisePhone(input));
    }

    [Theory]
    [InlineData("0812440001")]
    [InlineData("254812440001")]
    [InlineData("071244000")]
    [InlineData("07124400012")]
    [InlineData("abcdefghij")]
    [InlineData("+1 202 555 0100")]
    public void Phone_Rejects_invalidNumbers(string input)
    {
        Assert.NotNull(MpesaDetailsValidation.ValidatePhone(input));
        Assert.Null(MpesaDetailsValidation.NormalisePhone(input));
    }

    // ── Payee name / instructions ───────────────────────────

    [Fact]
    public void PayeeName_Allows_60_and_rejects_61()
    {
        Assert.Null(MpesaDetailsValidation.ValidatePayeeName(new string('a', 60)));
        Assert.NotNull(MpesaDetailsValidation.ValidatePayeeName(new string('a', 61)));
        Assert.Null(MpesaDetailsValidation.ValidatePayeeName(null));
    }

    [Fact]
    public void Instructions_Allow_300_and_rejects_301()
    {
        Assert.Null(MpesaDetailsValidation.ValidateInstructions(new string('a', 300)));
        Assert.NotNull(MpesaDetailsValidation.ValidateInstructions(new string('a', 301)));
        Assert.Null(MpesaDetailsValidation.ValidateInstructions(null));
    }

    // ── Whole configuration ─────────────────────────────────

    [Fact]
    public void Validate_EnablingPaybillWithoutAccount_ReportsTheAccountField()
    {
        var errors = MpesaDetailsValidation.Validate(new SavePaymentConfigRequest
        {
            ManualMpesaEnabled = true,
            MpesaAccountType = "paybill",
            MpesaPaybillNumber = "247247",
            MpesaPaybillAccount = null
        });

        Assert.Contains(MpesaDetailsValidation.PaybillAccountField, errors.Keys);
    }

    [Fact]
    public void Validate_EnablingSendMoneyWithoutPhone_ReportsThePhoneField()
    {
        var errors = MpesaDetailsValidation.Validate(new SavePaymentConfigRequest
        {
            ManualMpesaEnabled = true,
            MpesaAccountType = "send_money",
            MpesaPhoneNumber = "not-a-number"
        });

        Assert.Contains(MpesaDetailsValidation.PhoneNumberField, errors.Keys);
    }

    [Fact]
    public void Validate_Disabled_IgnoresMissingMethod()
    {
        var errors = MpesaDetailsValidation.Validate(new SavePaymentConfigRequest
        {
            ManualMpesaEnabled = false,
            MpesaAccountType = "paybill"
        });

        Assert.Empty(errors);
    }
}
