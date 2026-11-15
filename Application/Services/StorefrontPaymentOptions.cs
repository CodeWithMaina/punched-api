namespace PunchedApi.Application.Services;

/// <summary>
/// The storefront payment options a business can offer, and the one place a customer's
/// choice is parsed and checked against the business's own configuration.
///
/// <para><b>Why this exists.</b> Hiding a control in the storefront is a convenience, not
/// a rule: the request that creates an appointment carries the customer's choice, so the
/// server has to decide whether that choice is one this business actually offers. This
/// class holds that decision in one place so the public payment-details endpoint, the
/// booking path and the tests cannot drift apart.</para>
/// </summary>
public static class StorefrontPaymentOptions
{
    /// <summary>The customer pays during booking ("Payment Included").</summary>
    public const string PayUpfront = "pay_upfront";

    /// <summary>The customer books now and settles after the service.</summary>
    public const string PayAfterService = "pay_after_service";

    /// <summary>
    /// Parses a wire value, tolerating the spellings a hand-written client might send
    /// (separators and casing are ignored). False for a value that names no known option —
    /// never a default, because defaulting a payment choice silently would be wrong.
    /// </summary>
    public static bool TryParse(string? value, out string option)
    {
        option = string.Empty;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var normalised = new string(value.Where(char.IsLetter).ToArray()).ToLowerInvariant();
        switch (normalised)
        {
            case "payupfront":
            case "paymentincluded":
            case "upfront":
                option = PayUpfront;
                return true;
            case "payafterservice":
            case "afterservice":
            case "later":
                option = PayAfterService;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Human label for a resolved option, for messages the customer reads.</summary>
    public static string Label(string option) =>
        option == PayAfterService ? "pay after the service" : "pay during booking";

    /// <summary>The outcome of checking a requested option against a business's policy.</summary>
    public readonly record struct Resolution(bool Ok, string? Option, string? ErrorCode, string? ErrorMessage);

    /// <summary>
    /// Decides how this booking is paid.
    ///
    /// <para>Order of checks matters: a business that takes no bookings is refused first
    /// (whatever the customer asked for), then an explicit choice that the business has
    /// switched off, and only then whether a choice was needed at all. An omitted option is
    /// applied automatically when exactly one is enabled, and is refused when both are —
    /// picking one for the customer would quietly decide when their money is taken.</para>
    /// </summary>
    public static Resolution Resolve(bool payUpfrontEnabled, bool payAfterServiceEnabled, string? requested)
    {
        if (!payUpfrontEnabled && !payAfterServiceEnabled)
            return new Resolution(false, null, "BOOKING_UNAVAILABLE",
                "This business is not taking online bookings right now. Please contact them directly.");

        var hasChoice = !string.IsNullOrWhiteSpace(requested);
        if (!hasChoice)
        {
            if (payUpfrontEnabled && payAfterServiceEnabled)
                return new Resolution(false, null, "PAYMENT_OPTION_REQUIRED",
                    "Choose how you would like to pay: during booking, or after the service.");
            return new Resolution(true, payUpfrontEnabled ? PayUpfront : PayAfterService, null, null);
        }

        if (!TryParse(requested, out var option))
            return new Resolution(false, null, "VALIDATION_ERROR",
                "paymentOption must be 'pay_upfront' or 'pay_after_service'.");

        if (option == PayUpfront && !payUpfrontEnabled)
            return new Resolution(false, null, "PAYMENT_OPTION_DISABLED",
                "This business does not take payment during booking. Please choose to pay after the service.");

        if (option == PayAfterService && !payAfterServiceEnabled)
            return new Resolution(false, null, "PAYMENT_OPTION_DISABLED",
                "This business does not offer pay-after-service bookings. Please choose to pay during booking.");

        return new Resolution(true, option, null, null);
    }
}
