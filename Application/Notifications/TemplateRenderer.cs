using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PunchedApi.Application.Notifications;

public sealed partial class TemplateRenderer : ITemplateRenderer
{
    private readonly ILogger<TemplateRenderer> _logger;

    public TemplateRenderer(ILogger<TemplateRenderer> logger) => _logger = logger;

    public string RenderText(string template, IReadOnlyDictionary<string, object?> data) =>
        Render(template, data, encodeHtml: false);

    public string RenderHtml(string template, IReadOnlyDictionary<string, object?> data) =>
        Render(template, data, encodeHtml: true);

    private string Render(string template, IReadOnlyDictionary<string, object?> data, bool encodeHtml) =>
        TemplateToken().Replace(template, match =>
        {
            var key = match.Groups[1].Value;
            if (!data.TryGetValue(key, out var value))
            {
                _logger.LogWarning("Notification template token {Token} has no value.", key);
                return string.Empty;
            }

            var text = Format(value);
            return encodeHtml ? WebUtility.HtmlEncode(text) : text;
        });

    private static string Format(object? value) => value switch
    {
        null => string.Empty,
        JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => string.Empty,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? string.Empty,
        JsonElement element => element.ToString(),
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty
    };

    [GeneratedRegex(@"\{\{([A-Za-z][A-Za-z0-9_]*)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex TemplateToken();
}