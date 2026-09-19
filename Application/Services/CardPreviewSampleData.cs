namespace PunchedApi.Application.Services;

/// <summary>
/// Safe, non-realistic sample data used whenever a card design is previewed
/// (admin create/edit preview, business design selector, business dashboard).
///
/// It fills every supported template variable so an author can see how the card
/// will look without any real customer information ever leaving the database.
/// The same value set is used by admin and business previews so both tiers see
/// an identical card.
/// </summary>
public static class CardPreviewSampleData
{
    /// <summary>Business name shown on previews.</summary>
    public const string BusinessName = "Demo Coffee House";

    /// <summary>Optional business logo used by previews (empty ⇒ no logo).</summary>
    public const string BusinessLogoUrl = "";

    /// <summary>Optional business description used by previews.</summary>
    public const string BusinessDescription = "Speciality coffee, roasted daily.";

    /// <summary>Loyalty program / campaign name.</summary>
    public const string ProgramName = "Rewards Program";

    /// <summary>Sample customer name (never a real customer).</summary>
    public const string CustomerName = "John Doe";

    /// <summary>Sample card name.</summary>
    public const string CardName = "Rewards Card";

    /// <summary>Sample reward description.</summary>
    public const string RewardName = "Free Coffee";

    /// <summary>Total stamp slots demonstrated by a preview (10 → "5 / 10").</summary>
    public const int TotalStamps = 10;

    /// <summary>Filled stamp slots demonstrated by a preview (5 → "5 / 10").</summary>
    public const int CompletedStamps = 5;

    /// <summary>
    /// Builds a render context from the sample data, applying caller overrides
    /// for the fields a business is allowed to brand (business name/logo).
    /// </summary>
    public static CardTemplateRenderer.CardRenderContext CreateContext(
        string? businessName = null,
        string? businessLogoUrl = null,
        string? customerName = null,
        string? cardName = null,
        string? rewardName = null,
        int? totalStamps = null,
        int? completedStamps = null,
        string? programName = null)
    {
        var total = Math.Clamp(totalStamps ?? TotalStamps, 1, 100);
        return new CardTemplateRenderer.CardRenderContext
        {
            BusinessName = Coalesce(businessName, BusinessName),
            BusinessLogoUrl = businessLogoUrl ?? BusinessLogoUrl,
            BusinessDescription = BusinessDescription,
            CustomerName = Coalesce(customerName, CustomerName),
            CampaignName = Coalesce(programName, ProgramName),
            CardName = Coalesce(cardName, CardName),
            RewardName = Coalesce(rewardName, RewardName),
            TotalStamps = total,
            CompletedStamps = Math.Clamp(completedStamps ?? CompletedStamps, 0, total)
        };
    }

    private static string Coalesce(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}