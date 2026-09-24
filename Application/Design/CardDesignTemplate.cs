using System.Net;
using System.Text;
using PunchedApi.Application.Assets;

namespace PunchedApi.Application.Design;

/// <summary>
/// Turns a validated <see cref="CardDesignConfig"/> into a stamp-card HTML
/// template that flows through the *same* sanitizer + renderer pipeline as any
/// author-supplied design.
///
/// Why generate a template instead of rendering directly: preview and production
/// then share one implementation end to end (§15). A config-driven card and a
/// hand-authored card are indistinguishable to the renderer, so an admin preview
/// can never diverge from what a customer sees.
///
/// Safety: the output uses only tags/attributes/CSS properties on
/// <c>CardTemplateSanitizer</c>'s allow-lists, so sanitization is a no-op for
/// generated designs — anything unexpected would be a bug, and tests assert the
/// round-trip is stable.
/// </summary>
public static class CardDesignTemplate
{
    /// <summary>Display name given to the generated default design.</summary>
    public const string DefaultName = "Punched Default Card";

    /// <summary>
    /// Builds the template for a configuration. The caller must pass an
    /// already-validated config (see <see cref="CardDesignConfigValidator"/>).
    /// </summary>
    public static string Build(CardDesignConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var colors = config.Colors;
        var frame = config.Frame;

        var sb = new StringBuilder();

        // ── Card surface ────────────────────────────────────
        sb.Append("<section class=\"punched-card\" style=\"");
        sb.Append("background-color:").Append(Attr(colors.Surface)).Append(';');
        sb.Append("color:").Append(Attr(colors.Text)).Append(';');
        sb.Append("font-family:").Append(Attr(CardTypographyStacks.ToCssStack(config.Typography.Body))).Append(';');
        sb.Append("border-radius:").Append(frame.BorderRadius).Append("px;");
        if (frame.BorderWidth > 0)
        {
            sb.Append("border-width:").Append(frame.BorderWidth).Append("px;");
            sb.Append("border-style:solid;");
            sb.Append("border-color:").Append(Attr(frame.BorderColor ?? colors.Secondary)).Append(';');
        }
        sb.Append("padding:16px;");
        sb.Append("\">");

        AppendBackgroundLayer(sb, config);
        AppendHeader(sb, config);
        AppendProgress(sb, config);
        AppendStampGrid(sb, config);
        if (config.Layout.ShowRewardSection) AppendReward(sb, config);

        sb.Append("</section>");
        return sb.ToString();
    }

    /// <summary>
    /// Background artwork is emitted as an <c>img</c> rather than a CSS
    /// <c>background-image</c> because the sanitizer forbids <c>url()</c> in
    /// styles — which is also why a style can never escape the sandboxed iframe.
    /// </summary>
    private static void AppendBackgroundLayer(StringBuilder sb, CardDesignConfig config)
    {
        if (!string.Equals(config.Background.Type, "image", StringComparison.Ordinal)
            || config.Background.AssetId is not { } assetId)
            return;

        sb.Append("<img class=\"punched-card__background\" src=\"")
          .Append(Attr(CardAssetUrls.ContentPath(assetId)))
          .Append("\" alt=\"\" style=\"width:100%;height:100%;object-fit:cover;opacity:0.15;border-radius:")
          .Append(config.Frame.BorderRadius)
          .Append("px;\" />");
    }

    private static void AppendHeader(StringBuilder sb, CardDesignConfig config)
    {
        sb.Append("<header class=\"punched-card__header\" style=\"display:flex;align-items:center;gap:12px;margin-bottom:12px\">");

        if (config.Logo.AssetId is { } logoId)
        {
            sb.Append("<img class=\"punched-card__logo\" src=\"")
              .Append(Attr(CardAssetUrls.ContentPath(logoId)))
              .Append("\" alt=\"{{business.name}}\" style=\"width:48px;height:48px;object-fit:contain;border-radius:8px\" />");
        }

        sb.Append("<div class=\"punched-card__identity\">");
        sb.Append("<div class=\"punched-card__business\" style=\"font-family:\"")
          .Append(Attr(CardTypographyStacks.ToCssStack(config.Typography.Heading)))
          .Append(";font-size:18px;font-weight:600;color:")
          .Append(Attr(config.Colors.Secondary))
          .Append("\">{{business.name}}</div>");
        sb.Append("<div class=\"punched-card__campaign\" style=\"font-size:12px;color:\"")
          .Append(Attr(config.Colors.Secondary))
          .Append(";opacity:0.75\">{{campaign.name}}</div>");
        sb.Append("</div></header>");
    }

    private static void AppendProgress(StringBuilder sb, CardDesignConfig config)
    {
        sb.Append("<p class=\"punched-card__progress\" style=\"font-size:13px;margin:0 0 12px 0;color:")
          .Append(Attr(config.Colors.Text))
          .Append("\">{{card.completedStamps}} / {{card.totalStamps}} stamps</p>");
    }

    private static void AppendStampGrid(StringBuilder sb, CardDesignConfig config)
    {
        sb.Append("<div class=\"punched-card__grid\" style=\"display:grid;grid-template-columns:repeat(")
          .Append(config.Layout.StampGrid.Columns)
          .Append(",1fr);gap:8px;align-items:center\">");

        var usesIcons = config.Stamp.CompletedAssetId is not null || config.Stamp.EmptyAssetId is not null;

        sb.Append("{{#each stamps}}");
        sb.Append("<div class=\"stamp {{status}}\" style=\"display:flex;align-items:center;justify-content:center;width:100%;aspect-ratio:1;border-radius:9999px;");
        if (usesIcons)
        {
            sb.Append("background-color:").Append(Attr(config.Colors.Surface))
              .Append(";border-width:2px;border-style:solid;border-color:")
              .Append(Attr(config.Colors.Primary))
              .Append("\">{{iconTag}}");
        }
        else
        {
            sb.Append("background-color:").Append(Attr(config.Colors.Primary))
              .Append(";color:").Append(Attr(config.Colors.Surface))
              .Append(";font-size:14px\">{{glyph}}");
        }
        sb.Append("</div>{{/each}}</div>");
    }

    private static void AppendReward(StringBuilder sb, CardDesignConfig config)
    {
        sb.Append("<footer class=\"punched-card__reward\" style=\"margin-top:12px;padding-top:12px;border-top-width:1px;border-top-style:solid;border-top-color:")
          .Append(Attr(config.Colors.Accent))
          .Append(";font-size:13px;color:")
          .Append(Attr(config.Colors.Secondary))
          .Append("\">{{reward.name}}</footer>");
    }

    /// <summary>
    /// HTML-encodes interpolated values. Every value placed in the template comes
    /// from the validated config, but encoding keeps the generated markup
    /// well-formed even if a validator rule is later relaxed.
    /// </summary>
    private static string Attr(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
}
