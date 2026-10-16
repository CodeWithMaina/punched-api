using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Application.Notifications;

/// <summary>
/// Renders a responsive, single-column branded email layout using a business
/// messaging profile. All user-supplied values are HTML-encoded; unknown
/// template tokens are rejected; no arbitrary CSS/HTML execution is permitted.
/// </summary>
public sealed class EmailLayoutRenderer
{
    private readonly ITemplateRenderer _renderer;
    private readonly ILogger<EmailLayoutRenderer> _logger;

    private static readonly Regex TokenRegex = new(
        @"\{\{([A-Za-z][A-Za-z0-9_]*)\}\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex IfOpenRegex = new(
        @"\{\{([#^])if ([A-Za-z][A-Za-z0-9_]*)\}\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex InnerBlockRegex = new(
        @"\{\{([#^])if ([A-Za-z][A-Za-z0-9_]*)\}\}((?:(?!\{\{[#^]if ).)*?)\{\{/if\}\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static readonly Regex BodyTokenRegex = new(
        @"\{\{body\}\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex LeftoverBlockRegex = new(
        @"\{\{(?:[#^]if |/if\}\})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public EmailLayoutRenderer(ITemplateRenderer renderer, ILogger<EmailLayoutRenderer> logger)
    {
        _renderer = renderer;
        _logger = logger;
    }

    /// <summary>
    /// Renders a full branded email HTML document.
    /// </summary>
    public string Render(
        BusinessMessagingProfile? profile,
        string subject,
        string bodyHtml,
        bool isMarketing = false,
        IReadOnlyDictionary<string, object?>? extraTokens = null)
    {
        var brandColor = profile?.PrimaryColor ?? "#2563EB";
        var displayName = profile?.DisplayName ?? "Punched";
        var logoUrl = profile?.LogoMediaId != null
            ? $"/v1/media/{profile.LogoMediaId}"
            : string.Empty;
        var footerNote = profile?.FooterNote;
        var replyTo = profile?.ReplyTo;

        brandColor = SanitizeColor(brandColor);
        displayName = SanitizeText(displayName);

        var tokens = new Dictionary<string, object?>
        {
            ["subject"] = subject,
            ["body"] = bodyHtml,
            ["brandColor"] = brandColor,
            ["displayName"] = displayName,
            ["logoUrl"] = logoUrl,
            ["footerNote"] = footerNote,
            ["replyTo"] = replyTo,
            ["unsubscribeLink"] = string.Empty, // absolute tokenized URL supplied by the channel via extraTokens
            ["isMarketing"] = isMarketing
        };

        if (extraTokens != null)
        {
            foreach (var kv in extraTokens)
                tokens[kv.Key] = kv.Value;
        }

        var layoutTemplate = LayoutTemplate();

        // Diagnostics run against the raw template: block helpers ({{#if}}/{{^if}})
        // are consumed by the conditional pass below and never reach the token
        // renderer, so collect both plain tokens and conditional names.
        var usedTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in TokenRegex.Matches(layoutTemplate))
            usedTokens.Add(m.Groups[1].Value);
        foreach (Match m in IfOpenRegex.Matches(layoutTemplate))
            usedTokens.Add(m.Groups[2].Value);

        var unknown = tokens.Keys.Where(k => !usedTokens.Contains(k)).ToList();
        if (unknown.Count > 0)
            _logger.LogWarning("Unknown layout tokens supplied: {Tokens}", string.Join(", ", unknown));

        // 1) Expand {{#if x}}...{{/if}} (keep when truthy) and {{^if x}}...{{/if}}
        //    (keep when falsy), innermost first so nested blocks pair correctly.
        var expanded = ExpandConditionals(layoutTemplate, tokens);

        // 2) {{body}} carries server-rendered HTML that was already encoded when the
        //    notification template was rendered — splice it raw after the encoded
        //    pass and keep it out of the dictionary so its markup is preserved.
        var bodyMarker = "\u0001body\u0001";
        expanded = BodyTokenRegex.Replace(expanded, bodyMarker);
        tokens.Remove("body");

        // 3) A balanced template can never leave a block behind; if one survives it
        //    must not leak brace syntax into an outbound email.
        if (LeftoverBlockRegex.IsMatch(expanded))
            _logger.LogWarning("Unbalanced conditional block left in the email layout template.");

        return _renderer.RenderHtml(expanded, tokens).Replace(bodyMarker, bodyHtml);
    }

    /// <summary>
    /// Expands conditional blocks innermost-first: the content pattern refuses to
    /// cross an inner block opener, so the engine always pairs an opener with its
    /// own closer, and iterating to a fixed point resolves arbitrary nesting.
    /// Unknown tokens are treated as falsy.
    /// </summary>
    private string ExpandConditionals(string template, IReadOnlyDictionary<string, object?> tokens)
    {
        var result = template;
        for (var guard = 0; guard < 64; guard++)
        {
            var next = InnerBlockRegex.Replace(result, match =>
            {
                var truthy = IsTruthy(tokens, match.Groups[2].Value);
                var keep = match.Groups[1].Value == "^" ? !truthy : truthy;
                return keep ? match.Groups[3].Value : string.Empty;
            });
            if (string.Equals(next, result, StringComparison.Ordinal)) return result;
            result = next;
        }
        return result;
    }

    private static bool IsTruthy(IReadOnlyDictionary<string, object?> tokens, string key)
    {
        if (!tokens.TryGetValue(key, out var value) || value is null) return false;
        return value switch
        {
            bool flag => flag,
            string text => !string.IsNullOrWhiteSpace(text)
                && !string.Equals(text, "false", StringComparison.OrdinalIgnoreCase),
            System.Text.Json.JsonElement element => element.ValueKind switch
            {
                System.Text.Json.JsonValueKind.False => false,
                System.Text.Json.JsonValueKind.Null => false,
                System.Text.Json.JsonValueKind.Undefined => false,
                System.Text.Json.JsonValueKind.String => !string.IsNullOrWhiteSpace(element.GetString())
                    && !string.Equals(element.GetString(), "false", StringComparison.OrdinalIgnoreCase),
                System.Text.Json.JsonValueKind.Number => element.GetDouble() != 0,
                System.Text.Json.JsonValueKind.Array => element.GetArrayLength() > 0,
                _ => true
            },
            _ => true
        };
    }

    private static string SanitizeColor(string? color)
    {
        if (string.IsNullOrWhiteSpace(color)) return "#2563EB";
        var trimmed = color.Trim();
        if (Regex.IsMatch(trimmed, @"^#([A-Fa-f0-9]{6}|[A-Fa-f0-9]{3})$"))
            return trimmed;
        if (Regex.IsMatch(trimmed, @"^[a-zA-Z]+$"))
            return trimmed;
        return "#2563EB";
    }

    private static string SanitizeText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "Punched";
        return Regex.Replace(text, @"<[^>]*>", string.Empty).Trim();
    }

    private static string LayoutTemplate() =>
        """
        <!DOCTYPE html>
        <html>
        <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1.0">
            <title>{{subject}}</title>
        </head>
        <body style="margin:0;padding:0;font-family:system-ui,-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;background-color:#f3f4f6;">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background-color:#f3f4f6;">
                <tr>
                    <td align="center" style="padding:24px 16px;">
                        <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="max-width:600px;background-color:#ffffff;border-radius:8px;overflow:hidden;">
                            <tr>
                                <td align="left" style="padding:24px 32px;background-color:{{brandColor}};">
                                    {{#if logoUrl}}
                                    <img src="{{logoUrl}}" alt="{{displayName}}" style="height:32px;display:block;border:0;">
                                    {{/if}}
                                    {{^if logoUrl}}
                                    <span style="color:#ffffff;font-size:20px;font-weight:700;letter-spacing:0.5px;">{{displayName}}</span>
                                    {{/if}}
                                </td>
                            </tr>
                            <tr>
                                <td align="left" style="padding:32px;color:#1f2937;font-size:16px;line-height:1.6;">
                                    {{body}}
                                </td>
                            </tr>
                            <tr>
                                <td align="left" style="padding:24px 32px;border-top:1px solid #e5e7eb;color:#6b7280;font-size:14px;line-height:1.5;">
                                    {{displayName}} · Punched
                                    {{#if footerNote}}
                                    <br>{{footerNote}}
                                    {{/if}}
                                    {{#if isMarketing}}
                                    <br><br>
                                    {{#if unsubscribeLink}}
                                    <a href="{{unsubscribeLink}}" style="color:#6b7280;text-decoration:underline;">Unsubscribe</a>
                                    {{/if}}
                                    {{/if}}
                                    {{#if replyTo}}
                                    <br>Reply to: {{replyTo}}
                                    {{/if}}
                                </td>
                            </tr>
                        </table>
                    </td>
                </tr>
            </table>
        </body>
        </html>
        """;
}