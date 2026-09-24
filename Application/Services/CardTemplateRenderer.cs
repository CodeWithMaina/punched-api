using System.Net;
using System.Text.RegularExpressions;

namespace PunchedApi.Application.Services;

/// <summary>
/// The single rendering pipeline for stamp card HTML templates.
///
/// Templates contain only whitelisted variables (<see cref="TemplateVariables"/>)
/// and one optional loop block (<c>{{#each stamps}}…{{/each}}</c>).
///
/// Security model:
///  • Input is always the output of <see cref="CardTemplateSanitizer.Sanitize"/> —
///    no scripts, event handlers or dangerous URLs exist at render time.
///  • Every variable value is HTML-encoded before substitution, so customer or
///    business data can never inject markup.
///  • The one variable that emits markup — <c>{{stamps}}</c> — is generated here
///    from trusted, app-controlled data (filled/empty CSS classes), never from
///    user input.
///  • Unknown variables render as empty strings; unknown syntax is left alone.
///
/// The same method powers live previews and real renders, so previews are
/// always accurate.
/// </summary>
public static partial class CardTemplateRenderer
{
    /// <summary>All template variables available to business designers.</summary>
    public static readonly IReadOnlyList<string> AvailableVariables = new[]
    {
        "business.name", "business.logo", "business.description",
        "customer.name",
        "campaign.name",
        "card.name", "card.totalStamps", "card.completedStamps",
        "reward.name",
        "stamps"
    };

    [GeneratedRegex("\\{\\{#each stamps\\}\\}(.*?)\\{\\{/each\\}\\}", RegexOptions.Singleline | RegexOptions.Compiled)]
    private static partial Regex EachStampsRegex();

    [GeneratedRegex("\\{\\{\\s*([a-zA-Z][a-zA-Z0-9_.]*)\\s*\\}\\}", RegexOptions.Compiled)]
    private static partial Regex VariableRegex();

    /// <summary>
    /// Data source for rendering. All values are application-controlled.
    /// </summary>
    public sealed class CardRenderContext
    {
        public string BusinessName { get; set; } = string.Empty;
        public string? BusinessLogoUrl { get; set; }
        public string? BusinessDescription { get; set; }
        public string CustomerName { get; set; } = "Peter Maina";
        public string CampaignName { get; set; } = string.Empty;
        public string CardName { get; set; } = string.Empty;
        public int TotalStamps { get; set; } = 10;
        public int CompletedStamps { get; set; } = 4;
        public string RewardName { get; set; } = string.Empty;
        /// <summary>Filled-class / empty-class CSS names configured by the template author.</summary>
        public string FilledClass { get; set; } = "filled";
        public string EmptyClass { get; set; } = "empty";

        /// <summary>
        /// Short glyph rendered inside an unearned stamp slot. Null/empty leaves
        /// the slot blank (the historical behaviour).
        /// </summary>
        public string? StampEmptyGlyph { get; set; }

        /// <summary>Short glyph rendered inside an earned stamp slot.</summary>
        public string? StampCompletedGlyph { get; set; }

        /// <summary>
        /// App-relative URL of the unearned stamp icon (an uploaded
        /// <see cref="Domain.Entities.CardAsset"/>), or null when none is configured.
        /// </summary>
        public string? StampEmptyIconUrl { get; set; }

        /// <summary>App-relative URL of the earned stamp icon, or null.</summary>
        public string? StampCompletedIconUrl { get; set; }
    }

    /// <summary>
    /// Renders a (sanitized) template with the given context. This is the only
    /// rendering path in the system — previews and production renders share it.
    /// </summary>
    public static string Render(string template, CardRenderContext context)
    {
        if (string.IsNullOrWhiteSpace(template)) return string.Empty;

        var values = BuildValues(context);
        var total = Math.Max(0, context.TotalStamps);
        var completed = Math.Clamp(context.CompletedStamps, 0, total);

        // 1. Expand the {{#each stamps}} block (loop body repeated per stamp slot).
        var html = EachStampsRegex().Replace(template, match =>
        {
            var body = match.Groups[1].Value;
            var sb = new System.Text.StringBuilder();
            for (var position = 1; position <= total; position++)
            {
                var filled = position <= completed;
                sb.Append(body
                    .Replace("{{position}}", position.ToString())
                    .Replace("{{status}}", filled ? context.FilledClass : context.EmptyClass)
                    .Replace("{{filled}}", filled ? "true" : "false")
                    .Replace("{{glyph}}", WebUtility.HtmlEncode(
                        (filled ? context.StampCompletedGlyph : context.StampEmptyGlyph) ?? string.Empty))
                    .Replace("{{icon}}", WebUtility.HtmlEncode(
                        (filled ? context.StampCompletedIconUrl : context.StampEmptyIconUrl) ?? string.Empty))
                    .Replace("{{iconTag}}", BuildStampIconTag(
                        filled ? context.StampCompletedIconUrl : context.StampEmptyIconUrl)));
            }
            return sb.ToString();
        });

        // 2. Substitute whitelisted variables (HTML-encode every value).
        html = VariableRegex().Replace(html, match =>
        {
            var key = match.Groups[1].Value;
            if (key == "stamps") return BuildStampsMarkup(total, completed, context);
            return values.TryGetValue(key, out var value) ? WebUtility.HtmlEncode(value ?? string.Empty) : string.Empty;
        });

        return html;
    }

    private static Dictionary<string, string?> BuildValues(CardRenderContext c) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["business.name"] = c.BusinessName,
        ["business.logo"] = c.BusinessLogoUrl ?? string.Empty,
        ["business.description"] = c.BusinessDescription ?? string.Empty,
        ["customer.name"] = c.CustomerName,
        ["campaign.name"] = c.CampaignName,
        ["card.name"] = c.CardName,
        ["card.totalStamps"] = c.TotalStamps.ToString(),
        ["card.completedStamps"] = c.CompletedStamps.ToString(),
        ["reward.name"] = c.RewardName
    };

    /// <summary>
    /// Builds the safe markup emitted by <c>{{stamps}}</c> — filled slots first,
    /// then empty slots, with author-configurable CSS classes.
    /// </summary>
    private static string BuildStampsMarkup(int total, int completed, CardRenderContext context)
    {
        var sb = new System.Text.StringBuilder();
        for (var position = 1; position <= total; position++)
        {
            var filled = position <= completed;
            sb.Append("<span class=\"stamp ")
              .Append(filled ? WebUtility.HtmlEncode(context.FilledClass) : WebUtility.HtmlEncode(context.EmptyClass))
              .Append("\" data-position=\"")
              .Append(position)
              .Append("\"></span>");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Builds the markup emitted by <c>{{iconTag}}</c> inside a
    /// <c>{{#each stamps}}</c> block.
    ///
    /// The URL is application-controlled (derived from a validated
    /// <see cref="Domain.Entities.CardAsset"/> id, never from an author string) and
    /// is re-checked against <see cref="CardTemplateSanitizer.IsSafeUrl"/> before
    /// being emitted, so this variable can never become an injection point. When no
    /// icon is configured it resolves to the empty string and the author's glyph or
    /// colour fallback is used instead.
    /// </summary>
    private static string BuildStampIconTag(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !CardTemplateSanitizer.IsSafeUrl(url))
            return string.Empty;

        return "<img src=\"" + WebUtility.HtmlEncode(url) + "\" alt=\"\" />";
    }
}
