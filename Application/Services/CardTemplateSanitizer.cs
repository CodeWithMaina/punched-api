using System.Net;
using System.Text.RegularExpressions;

namespace PunchedApi.Application.Services;

/// <summary>
/// Whitelist-based HTML sanitizer for business-uploaded stamp card templates.
///
/// Uploaded HTML is untrusted user input. This sanitizer guarantees that a
/// stored <see cref="PunchedApi.Domain.Entities.CardDesign.HtmlTemplate"/>
/// contains only presentation markup:
///
///  • Only whitelisted tags survive — <c>script</c>, <c>iframe</c>, <c>object</c>,
///    <c>embed</c>, <c>svg</c>, <c>math</c>, <c>form</c>, <c>link</c>, <c>meta</c>,
///    <c>base</c>, <c>style</c> and comments are removed entirely (including their
///    inner content for script/iframe/style).
///  • Only whitelisted attributes survive — <c>on*</c> event handlers, <c>srcdoc</c>,
///    <c>formaction</c> etc. are stripped.
///  • URLs (<c>href</c>/<c>src</c>) must be http(s), a same-page anchor, or an
///    inline <c>data:image/...</c> payload — <c>javascript:</c> and friends are removed.
///  • CSS via the <c>style</c> attribute is filtered through a safe-property
///    whitelist (no <c>url()</c> to arbitrary hosts, no <c>expression()</c>/<c>behavior</c>…).
///
/// The output is an HTML <em>fragment</em> (never a full document) and is always
/// rendered inside a sandboxed iframe on the client.
/// </summary>
public static partial class CardTemplateSanitizer
{
    private static readonly HashSet<string> AllowedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "div", "span", "p", "a", "img", "br", "hr", "strong", "b", "em", "i", "u", "small",
        "h1", "h2", "h3", "h4", "h5", "h6", "ul", "ol", "li", "table", "thead", "tbody",
        "tr", "th", "td", "section", "header", "footer", "main", "article", "figure", "figcaption"
    };

    private static readonly HashSet<string> AllowedAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "class", "id", "title", "alt", "width", "height", "href", "src", "style"
    };

    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled)]
    private static partial Regex CommentRegex();

    [GeneratedRegex("<(script|style|iframe|object|embed|template|noscript|svg|math|form|base|link|meta)(?:[^>]*?)(?:/>|>.*?</\\1\\s*>)", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex DangerousElementRegex();

    [GeneratedRegex("</?([a-zA-Z][a-zA-Z0-9-]*)((?:\\s+[^<>]*?)?)(/?)>", RegexOptions.Singleline | RegexOptions.Compiled)]
    private static partial Regex TagRegex();

    [GeneratedRegex("([a-zA-Z-]+)\\s*=\\s*(\"[^\"]*\"|'[^']*'|[^\\s\"'<>]+)", RegexOptions.Compiled)]
    private static partial Regex AttributeRegex();

    [GeneratedRegex("([a-zA-Z-]+)\\s*:\\s*([^;]*)", RegexOptions.Compiled)]
    private static partial Regex CssPropertyRegex();

    [GeneratedRegex("expression\\s*\\(|behavior\\s*:|@import|javascript\\s*:", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex CssDangerousRegex();

    [GeneratedRegex("^(?:https?:)?//[^\\s]+$|^#[\\w-]+$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex SafeUrlRegex();

    [GeneratedRegex("^data:image/(?:png|jpe?g|gif|webp);base64,[a-zA-Z0-9+/=]+$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex SafeDataImageRegex();

    /// <summary>
    /// Sanitizes an uploaded HTML template fragment. Returns the sanitized HTML
    /// (never null — invalid input collapses to an empty string).
    /// </summary>
    public static string Sanitize(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;

        var input = html.Trim();

        // 1. Drop comments and dangerous containers (with their contents).
        input = CommentRegex().Replace(input, string.Empty);
        input = DangerousElementRegex().Replace(input, string.Empty);

        // 2. Walk every remaining tag and rebuild it from whitelists only.
        return TagRegex().Replace(input, match =>
        {
            var tagName = match.Groups[1].Value.ToLowerInvariant();
            if (!AllowedTags.Contains(tagName)) return string.Empty; // strip unknown tags, keep text

            var rawAttributes = match.Groups[2].Value;
            var selfClosing = match.Groups[3].Value.Length > 0 || tagName is "br" or "hr" or "img";
            var attributes = BuildAttributes(tagName, rawAttributes);

            return selfClosing
                ? $"<{tagName}{attributes} />"
                : $"<{tagName}{attributes}>";
        });
    }

    private static string BuildAttributes(string tagName, string rawAttributes)
    {
        if (string.IsNullOrWhiteSpace(rawAttributes)) return string.Empty;

        var sb = new System.Text.StringBuilder();
        foreach (Match attr in AttributeRegex().Matches(rawAttributes))
        {
            var name = attr.Groups[1].Value.ToLowerInvariant();
            var value = attr.Groups[2].Value.Trim('"', '\'');

            if (!AllowedAttributes.Contains(name)) continue;
            if (name is "href" or "src" && !IsSafeUrl(value)) continue;
            if (name == "style")
            {
                if (value.Length > 2000) continue;
                value = FilterStyle(value);
                if (value.Length == 0) continue;
            }

            sb.Append(' ').Append(name).Append("=\"")
              .Append(WebUtility.HtmlEncode(value))
              .Append('"');
        }
        return sb.ToString();
    }

    private static bool IsSafeUrl(string url) =>
        SafeUrlRegex().IsMatch(url) || SafeDataImageRegex().IsMatch(url);

    private static readonly HashSet<string> AllowedCssProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "background", "background-color", "background-size", "background-position", "background-repeat",
        "border", "border-radius", "border-color", "border-width", "border-style", "border-collapse",
        "box-shadow", "color", "display", "flex", "flex-direction", "flex-wrap", "gap", "grid",
        "grid-template-columns", "grid-template-rows", "align-items", "justify-content", "font",
        "font-family", "font-size", "font-weight", "font-style", "letter-spacing", "line-height",
        "margin", "margin-top", "margin-right", "margin-bottom", "margin-left", "max-width", "max-height",
        "min-width", "min-height", "object-fit", "opacity", "overflow", "padding", "padding-top",
        "padding-right", "padding-bottom", "padding-left", "text-align", "text-decoration",
        "text-shadow", "text-transform", "vertical-align", "white-space", "width", "height",
        "list-style", "list-style-type", "word-break", "word-wrap", "aspect-ratio"
    };

    /// <summary>
    /// Filters a CSS declaration list against the safe-property whitelist and
    /// returns the filtered result (unsafe declarations are dropped).
    /// </summary>
    private static string FilterStyle(string style)
    {
        if (CssDangerousRegex().IsMatch(style)) return string.Empty;

        var sb = new System.Text.StringBuilder();
        foreach (Match prop in CssPropertyRegex().Matches(style))
        {
            var name = prop.Groups[1].Value.Trim();
            var value = prop.Groups[2].Value.Trim();
            if (!AllowedCssProperties.Contains(name)) continue;
            if (value.Contains("url(", StringComparison.OrdinalIgnoreCase)) continue;

            sb.Append(name.ToLowerInvariant()).Append(':').Append(value).Append("; ");
        }
        return sb.ToString();
    }

    /// <summary>Validates a raw template before persistence (structural checks).</summary>
    public static (bool IsValid, string? Error) Validate(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return (false, "Template HTML must not be empty.");
        if (html.Length > 50000)
            return (false, "Template HTML must not exceed 50,000 characters.");

        var variableCount = 0;
        var index = 0;
        while ((index = html.IndexOf("{{", index, StringComparison.Ordinal)) >= 0)
        {
            variableCount++;
            index += 2;
            if (variableCount > 200)
                return (false, "Template contains too many variables (max 200).");
        }

        return (true, null);
    }
}
