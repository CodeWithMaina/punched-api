using System.Text.Json;
using System.Text.Json.Serialization;

namespace PunchedApi.Application.Design;

/// <summary>
/// The structured, versioned *presentation* model for a stamp card.
///
/// This is deliberately data, not code: new visual properties can be introduced
/// without a database change, and the whole object is validated server-side by
/// <see cref="CardDesignConfigValidator"/> before it is ever stored or rendered.
///
/// It contains no loyalty data whatsoever. Required stamps, rewards, progress and
/// redemption live in the domain entities and can never be expressed here — which
/// is what structurally prevents a design change from mutating loyalty state.
/// </summary>
public sealed class CardDesignConfig
{
    /// <summary>Highest config schema version this build understands.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Schema version of this configuration object. Bumped when the shape changes.</summary>
    [JsonPropertyName("version")]
    public int Version { get; set; } = CurrentVersion;

    [JsonPropertyName("background")]
    public CardBackgroundConfig Background { get; set; } = new();

    [JsonPropertyName("colors")]
    public CardColorsConfig Colors { get; set; } = new();

    [JsonPropertyName("logo")]
    public CardAssetReference Logo { get; set; } = new();

    [JsonPropertyName("stamp")]
    public CardStampConfig Stamp { get; set; } = new();

    [JsonPropertyName("typography")]
    public CardTypographyConfig Typography { get; set; } = new();

    [JsonPropertyName("layout")]
    public CardLayoutConfig Layout { get; set; } = new();

    [JsonPropertyName("frame")]
    public CardFrameConfig Frame { get; set; } = new();

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    /// <summary>Serialises to the compact JSON stored in <c>card_designs.config_json</c>.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    /// <summary>
    /// Parses stored JSON. Returns null when the payload is absent or unreadable —
    /// callers must then fall back to the HTML template, never throw at render
    /// time because of bad stored data.
    /// </summary>
    public static CardDesignConfig? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<CardDesignConfig>(json, SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// A professional, fully-populated default design (§17 safe defaults): a
    /// business can create a usable branded card before uploading any artwork.
    /// </summary>
    public static CardDesignConfig CreateDefault(
        string primary = "#059669",
        string secondary = "#111827",
        string accent = "#7C3AED",
        string text = "#111827",
        string surface = "#FFFFFF",
        int stampColumns = 5,
        int borderRadius = 16) => new()
        {
            Version = CurrentVersion,
            Background = new CardBackgroundConfig { Type = "color", Value = surface },
            Colors = new CardColorsConfig
            {
                Primary = primary,
                Secondary = secondary,
                Accent = accent,
                Text = text,
                Surface = surface
            },
            Stamp = new CardStampConfig(),
            Typography = new CardTypographyConfig(),
            Layout = new CardLayoutConfig
            {
                StampGrid = new CardStampGridConfig { Columns = stampColumns }
            },
            Frame = new CardFrameConfig { BorderRadius = borderRadius }
        };

    /// <summary>Every asset id referenced anywhere in this config (used for ownership checks).</summary>
    public IEnumerable<Guid> ReferencedAssetIds()
    {
        if (Logo.AssetId is { } logo) yield return logo;
        if (Background.AssetId is { } bg) yield return bg;
        if (Stamp.EmptyAssetId is { } empty) yield return empty;
        if (Stamp.CompletedAssetId is { } done) yield return done;
    }
}

/// <summary>Card backdrop.</summary>
public sealed class CardBackgroundConfig
{
    /// <summary><c>color</c> | <c>image</c> | <c>gradient</c>.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "color";

    /// <summary>CSS colour (for <c>color</c>/<c>gradient</c>) — validated hex only.</summary>
    [JsonPropertyName("value")]
    public string? Value { get; set; }

    /// <summary>Uploaded background asset (for <c>image</c>).</summary>
    [JsonPropertyName("assetId")]
    public Guid? AssetId { get; set; }
}

/// <summary>Brand palette. Every value is a validated CSS hex colour.</summary>
public sealed class CardColorsConfig
{
    [JsonPropertyName("primary")]
    public string Primary { get; set; } = "#059669";

    [JsonPropertyName("secondary")]
    public string Secondary { get; set; } = "#111827";

    [JsonPropertyName("accent")]
    public string Accent { get; set; } = "#7C3AED";

    [JsonPropertyName("text")]
    public string Text { get; set; } = "#111827";

    [JsonPropertyName("surface")]
    public string Surface { get; set; } = "#FFFFFF";
}

/// <summary>Reference to an uploaded <see cref="Domain.Entities.CardAsset"/>.</summary>
public sealed class CardAssetReference
{
    [JsonPropertyName("assetId")]
    public Guid? AssetId { get; set; }
}

/// <summary>Stamp slot appearance. Icons are optional — a coloured slot is the fallback.</summary>
public sealed class CardStampConfig
{
    [JsonPropertyName("emptyAssetId")]
    public Guid? EmptyAssetId { get; set; }

    [JsonPropertyName("completedAssetId")]
    public Guid? CompletedAssetId { get; set; }

    /// <summary>Short glyph/text shown in an unearned slot (max 4 chars).</summary>
    [JsonPropertyName("emptyGlyph")]
    public string? EmptyGlyph { get; set; }

    /// <summary>Short glyph/text shown in an earned slot (max 4 chars).</summary>
    [JsonPropertyName("completedGlyph")]
    public string? CompletedGlyph { get; set; } = "★";
}

/// <summary>Typography choices, restricted to a server-side allow-list of stacks.</summary>
public sealed class CardTypographyConfig
{
    /// <summary>Allow-listed font stack key — see <see cref="CardTypographyStacks"/>.</summary>
    [JsonPropertyName("heading")]
    public string Heading { get; set; } = CardTypographyStacks.Sans;

    [JsonPropertyName("body")]
    public string Body { get; set; } = CardTypographyStacks.Sans;
}

/// <summary>Layout knobs. Extensible without a migration.</summary>
public sealed class CardLayoutConfig
{
    [JsonPropertyName("stampGrid")]
    public CardStampGridConfig StampGrid { get; set; } = new();

    /// <summary>Whether the reward section is rendered on the card face.</summary>
    [JsonPropertyName("showRewardSection")]
    public bool ShowRewardSection { get; set; } = true;
}

/// <summary>Stamp grid geometry.</summary>
public sealed class CardStampGridConfig
{
    [JsonPropertyName("columns")]
    public int Columns { get; set; } = 5;
}

/// <summary>Card frame / border.</summary>
public sealed class CardFrameConfig
{
    [JsonPropertyName("borderRadius")]
    public int BorderRadius { get; set; } = 16;

    [JsonPropertyName("borderWidth")]
    public int BorderWidth { get; set; }

    /// <summary>Border colour — validated hex only.</summary>
    [JsonPropertyName("borderColor")]
    public string? BorderColor { get; set; }
}

/// <summary>
/// Server-side allow-list of font stacks. Arbitrary font-family strings are a
/// mild injection/SSRF surface (remote <c>url()</c> fonts) so only these keys are
/// accepted, and they map to stacks that always resolve locally.
/// </summary>
public static class CardTypographyStacks
{
    public const string Sans = "sans";
    public const string Serif = "serif";
    public const string Mono = "mono";
    public const string Rounded = "rounded";

    /// <summary>All accepted stacks.</summary>
    public static readonly IReadOnlyList<string> All = new[] { Sans, Serif, Mono, Rounded };

    /// <summary>True when the key is allow-listed (case-insensitive).</summary>
    public static bool IsSupported(string? key) =>
        key != null && All.Contains(key.Trim().ToLowerInvariant(), StringComparer.Ordinal);

    /// <summary>Maps an allow-listed key to a CSS font stack that needs no network access.</summary>
    public static string ToCssStack(string? key) => key?.Trim().ToLowerInvariant() switch
    {
        Serif => "Georgia, 'Times New Roman', serif",
        Mono => "'SFMono-Regular', Consolas, 'Liberation Mono', monospace",
        Rounded => "'Trebuchet MS', 'Segoe UI', system-ui, sans-serif",
        _ => "system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif"
    };
}
