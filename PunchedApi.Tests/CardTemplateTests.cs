using PunchedApi.Application.Services;

namespace PunchedApi.Tests;

/// <summary>
/// Security and behavior tests for the stamp card HTML template pipeline
/// (CardTemplateSanitizer + CardTemplateRenderer).
/// </summary>
public class CardTemplateSanitizerAndRendererTests
{
    // ── Sanitization ────────────────────────────────────────

    [Fact]
    public void Sanitize_PreservesNestedClosingTags()
    {
        const string template = "<section><header><div>Brand</div></header><article><h2>Rewards</h2><p>Earn <strong>stamps</strong></p></article></section>";
        Assert.Equal(template, CardTemplateSanitizer.Sanitize(template));
    }

    [Fact]
    public void Sanitize_StripsScriptTagsAndContent()
    {
        var result = CardTemplateSanitizer.Sanitize("<div>Hello<script>alert(1)</script></div>");
        Assert.DoesNotContain("script", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alert", result);
        Assert.Contains("Hello", result);
    }

    [Fact]
    public void Sanitize_StripsIframeObjectEmbedSvg()
    {
        var result = CardTemplateSanitizer.Sanitize(
            "<div>ok</div><iframe src=\"https://evil.test\"></iframe><object></object><embed><svg onload=\"x\"></svg>");
        Assert.DoesNotContain("iframe", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("embed", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("svg", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ok", result);
    }

    [Fact]
    public void Sanitize_StripsEventHandlers()
    {
        var result = CardTemplateSanitizer.Sanitize("<div onclick=\"alert(1)\" onerror=\"x\" class=\"card\">hi</div>");
        Assert.DoesNotContain("onclick", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onerror", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("class=\"card\"", result);
    }

    [Fact]
    public void Sanitize_BlocksJavascriptUrls()
    {
        var result = CardTemplateSanitizer.Sanitize("<a href=\"javascript:alert(1)\">x</a><img src=\"javascript:alert(2)\">");
        Assert.DoesNotContain("javascript:", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sanitize_AllowsApprovedImages_AndBlocksArbitraryRemoteImageSources()
    {
        var html = "<img src=\"https://cdn.test/logo.png\" alt=\"logo\" />" +
                   "<img src=\"data:image/png;base64,iVBORw0KGgo=\" />" +
                   "<img src=\"{{business.logo}}\" />" +
                   "<img src=\"/v1/card-assets/00000000-0000-0000-0000-000000000001/content\" />";
        var result = CardTemplateSanitizer.Sanitize(html);
        Assert.DoesNotContain("https://cdn.test/logo.png", result);
        Assert.Contains("data:image/png;base64,iVBORw0KGgo=", result);
        Assert.Contains("{{business.logo}}", result);
        Assert.Contains("/v1/card-assets/00000000-0000-0000-0000-000000000001/content", result);
    }

    [Fact]
    public void Sanitize_FiltersUnsafeCss()
    {
        var result = CardTemplateSanitizer.Sanitize(
            "<div style=\"color:red;position:fixed;background:url(https://evil.test/x.png)\">x</div>");
        Assert.Contains("color:", result);
        Assert.DoesNotContain("position", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("evil.test", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sanitize_StripsUnknownTagsButKeepsText()
    {
        var result = CardTemplateSanitizer.Sanitize("<div><form><input></form>text</div>");
        Assert.DoesNotContain("<form", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<input", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("text", result);
    }

    [Fact]
    public void Sanitize_HtmlEncodesAttributeValues()
    {
        var result = CardTemplateSanitizer.Sanitize("<img title=\"a&b\" data-x=\"1\" />");
        Assert.Contains("a&amp;b", result);
        Assert.DoesNotContain("data-x", result);
    }

    [Fact]
    public void Validate_RejectsEmptyAndOversizedTemplates()
    {
        Assert.False(CardTemplateSanitizer.Validate("   ").IsValid);
        Assert.False(CardTemplateSanitizer.Validate(new string('x', 60000)).IsValid);
        Assert.True(CardTemplateSanitizer.Validate("<div>{{business.name}}</div>").IsValid);
    }

    // ── Rendering ───────────────────────────────────────────

    private static CardTemplateRenderer.CardRenderContext SampleContext() => new()
    {
        BusinessName = "Java House",
        BusinessLogoUrl = "https://cdn.test/logo.png",
        CustomerName = "Peter Maina",
        CampaignName = "Coffee Rewards",
        CardName = "Coffee Card",
        RewardName = "Buy 10 coffees, get 1 free",
        TotalStamps = 10,
        CompletedStamps = 4
    };

    [Fact]
    public void Render_SubstitutesWhitelistedVariables()
    {
        var html = CardTemplateRenderer.Render(
            "<div class=\"header\">{{business.name}}</div><div>{{customer.name}}</div><div>{{card.name}}</div><div>{{reward.name}}</div>",
            SampleContext());

        Assert.Contains("Java House", html);
        Assert.Contains("Peter Maina", html);
        Assert.Contains("Coffee Card", html);
        Assert.Contains("Buy 10 coffees, get 1 free", html);
    }

    [Fact]
    public void Render_EncodesVariableValues()
    {
        var context = SampleContext();
        context.CustomerName = "<script>alert(1)</script>";
        var html = CardTemplateRenderer.Render("<div>{{customer.name}}</div>", context);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void Render_UnknownVariablesRenderEmpty()
    {
        var html = CardTemplateRenderer.Render("<div>{{user.password}}{{internal.secret}}</div>", SampleContext());
        Assert.DoesNotContain("password", html);
        Assert.Equal("<div></div>", html);
    }

    [Fact]
    public void Render_EachStampsBlockExpandsWithStatus()
    {
        var template = "<div class=\"stamp-grid\">{{#each stamps}}<div class=\"stamp {{status}}\">{{position}}</div>{{/each}}</div>";
        var html = CardTemplateRenderer.Render(template, SampleContext());

        Assert.Contains("stamp filled\">1</div>", html);
        Assert.Contains("stamp empty\">10</div>", html);
        Assert.Equal(10, System.Text.RegularExpressions.Regex.Matches(html, "class=\"stamp ").Count);
        Assert.Equal(4, System.Text.RegularExpressions.Regex.Matches(html, "stamp filled").Count);
    }

    [Fact]
    public void Render_StampsVariableEmitsSafeGeneratedMarkup()
    {
        var html = CardTemplateRenderer.Render("<div class=\"stamps\">{{stamps}}</div>", SampleContext());
        Assert.Contains("<span class=\"stamp filled\" data-position=\"1\"", html);
        Assert.Contains("<span class=\"stamp empty\" data-position=\"10\"", html);
        Assert.Equal(10, System.Text.RegularExpressions.Regex.Matches(html, "<span class=\"stamp ").Count);
        Assert.Equal(4, System.Text.RegularExpressions.Regex.Matches(html, "class=\"stamp filled\"").Count);
        Assert.Contains("aspect-ratio:1", html);
    }

    [Fact]
    public void TemplateVariablesCatalog_CoversDocumentedVariables()
    {
        var expected = new[]
        {
            "business.name", "business.logo", "business.description", "customer.name",
            "campaign.name", "program.name", "card.name", "card.totalStamps", "card.completedStamps", "card.status",
            "stamps.current", "stamps.required", "reward.name", "reward.status", "stamps"
        };
        Assert.Equal(expected, CardTemplateRenderer.AvailableVariables);
    }

    [Fact]
    public void SanitizeThenRender_PipelineIsSafeEndToEnd()
    {
        var raw = "<div class=\"header\">{{business.name}}</div><script>evil()</script>";
        var sanitized = CardTemplateSanitizer.Sanitize(raw);
        var html = CardTemplateRenderer.Render(sanitized, SampleContext());
        Assert.Contains("Java House", html);
        Assert.DoesNotContain("evil", html, StringComparison.OrdinalIgnoreCase);
    }
}