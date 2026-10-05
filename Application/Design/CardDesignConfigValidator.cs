using System.Text.RegularExpressions;

namespace PunchedApi.Application.Design;

/// <summary>
/// Server-side validation for <see cref="CardDesignConfig"/>.
///
/// Front-end validation exists for UX only (§16): this is the boundary that
/// decides whether a configuration may be stored at all. Every rule here is a
/// pure function so it is exhaustively unit-testable without a database.
///
/// Asset *ownership* is intentionally not checked here — that needs the database
/// and lives in <c>CardDesignService</c>, which validates every asset id returned
/// by <see cref="CardDesignConfig.ReferencedAssetIds"/> against the caller's
/// business. This type only checks shape.
/// </summary>
public static partial class CardDesignConfigValidator
{
    /// <summary>Largest accepted stamp-grid column count.</summary>
    public const int MaxStampColumns = 10;

    /// <summary>Largest accepted border radius in CSS pixels.</summary>
    public const int MaxBorderRadius = 64;

    /// <summary>Largest accepted border width in CSS pixels.</summary>
    public const int MaxBorderWidth = 16;

    /// <summary>Largest accepted glyph length for a stamp slot.</summary>
    public const int MaxGlyphLength = 4;

    /// <summary>The only accepted background types.</summary>
    public static readonly IReadOnlyList<string> BackgroundTypes = new[] { "color", "image", "gradient" };

    [GeneratedRegex("^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$", RegexOptions.Compiled)]
    private static partial Regex HexColorRegex();

    /// <summary>True when <paramref name="value"/> is a CSS hex colour this system will emit.</summary>
    public static bool IsValidHexColor(string? value) =>
        !string.IsNullOrWhiteSpace(value) && HexColorRegex().IsMatch(value.Trim());

    /// <summary>
    /// Validates a configuration. Returns stable error codes so the API can
    /// surface them without leaking internals.
    /// </summary>
    public static CardDesignConfigValidation Validate(CardDesignConfig? config)
    {
        var errors = new List<string>();

        if (config == null)
            return new CardDesignConfigValidation(false, new[] { "CONFIG_REQUIRED" });

        if (config.Version < 1 || config.Version > CardDesignConfig.CurrentVersion)
            errors.Add("CONFIG_VERSION_UNSUPPORTED");

        ValidateColors(config, errors);
        ValidateBackground(config, errors);
        ValidateStamp(config, errors);
        ValidateTypography(config, errors);
        ValidateLayout(config, errors);
        ValidateFrame(config, errors);

        return new CardDesignConfigValidation(errors.Count == 0, errors);
    }

    private static void ValidateColors(CardDesignConfig config, List<string> errors)
    {
        var colors = config.Colors ??= new CardColorsConfig();
        if (!IsValidHexColor(colors.Primary)) errors.Add("INVALID_PRIMARY_COLOR");
        if (!IsValidHexColor(colors.Secondary)) errors.Add("INVALID_SECONDARY_COLOR");
        if (!IsValidHexColor(colors.Accent)) errors.Add("INVALID_ACCENT_COLOR");
        if (!IsValidHexColor(colors.Text)) errors.Add("INVALID_TEXT_COLOR");
        if (!IsValidHexColor(colors.Surface)) errors.Add("INVALID_SURFACE_COLOR");
    }

    private static void ValidateBackground(CardDesignConfig config, List<string> errors)
    {
        var background = config.Background ??= new CardBackgroundConfig();
        var type = background.Type?.Trim().ToLowerInvariant() ?? string.Empty;

        if (!BackgroundTypes.Contains(type, StringComparer.Ordinal))
        {
            errors.Add("INVALID_BACKGROUND_TYPE");
            return;
        }

        background.Type = type;

        switch (type)
        {
            case "color":
            case "gradient":
                if (!IsValidHexColor(background.Value)) errors.Add("INVALID_BACKGROUND_COLOR");
                // A colour background must never keep a stale image reference.
                background.AssetId = null;
                break;

            case "image":
                if (background.AssetId is null || background.AssetId == Guid.Empty)
                    errors.Add("BACKGROUND_ASSET_REQUIRED");
                background.Value = null;
                break;
        }
    }

    private static void ValidateStamp(CardDesignConfig config, List<string> errors)
    {
        var stamp = config.Stamp ??= new CardStampConfig();

        if (stamp.EmptyAssetId == Guid.Empty) stamp.EmptyAssetId = null;
        if (stamp.CompletedAssetId == Guid.Empty) stamp.CompletedAssetId = null;

        if (stamp.EmptyGlyph != null &&
            (stamp.EmptyGlyph.Length > MaxGlyphLength || !IsPlainGlyph(stamp.EmptyGlyph)))
            errors.Add("INVALID_EMPTY_GLYPH");

        if (stamp.CompletedGlyph != null &&
            (stamp.CompletedGlyph.Length > MaxGlyphLength || !IsPlainGlyph(stamp.CompletedGlyph)))
            errors.Add("INVALID_COMPLETED_GLYPH");
    }

    /// <summary>Glyphs may not contain markup delimiters or control characters.</summary>
    public static bool IsPlainGlyph(string glyph)
    {
        foreach (var ch in glyph)
        {
            if (char.IsControl(ch)) return false;
            if (ch is '<' or '>' or '&' or '"' or '\'' or '{' or '}' or ';') return false;
        }
        return true;
    }

    private static void ValidateTypography(CardDesignConfig config, List<string> errors)
    {
        var typography = config.Typography ??= new CardTypographyConfig();
        if (!CardTypographyStacks.IsSupported(typography.Heading)) errors.Add("INVALID_HEADING_TYPEFACE");
        if (!CardTypographyStacks.IsSupported(typography.Body)) errors.Add("INVALID_BODY_TYPEFACE");
    }

    private static void ValidateLayout(CardDesignConfig config, List<string> errors)
    {
        var layout = config.Layout ??= new CardLayoutConfig();
        var grid = layout.StampGrid ??= new CardStampGridConfig();

        if (grid.Columns < 1 || grid.Columns > MaxStampColumns)
            errors.Add("INVALID_STAMP_GRID_COLUMNS");
    }

    private static void ValidateFrame(CardDesignConfig config, List<string> errors)
    {
        var frame = config.Frame ??= new CardFrameConfig();

        if (frame.BorderRadius < 0 || frame.BorderRadius > MaxBorderRadius)
            errors.Add("INVALID_BORDER_RADIUS");

        if (frame.BorderWidth < 0 || frame.BorderWidth > MaxBorderWidth)
            errors.Add("INVALID_BORDER_WIDTH");

        if (!string.IsNullOrWhiteSpace(frame.BorderColor) && !IsValidHexColor(frame.BorderColor))
            errors.Add("INVALID_BORDER_COLOR");

        if (frame.BorderWidth == 0) frame.BorderColor = null;
        else if (string.IsNullOrWhiteSpace(frame.BorderColor)) frame.BorderColor = config.Colors.Secondary;
    }
}

/// <summary>Outcome of <see cref="CardDesignConfigValidator.Validate"/>.</summary>
/// <param name="IsValid">True when the configuration may be stored.</param>
/// <param name="Errors">Stable error codes (never messages containing user input).</param>
public sealed record CardDesignConfigValidation(bool IsValid, IReadOnlyList<string> Errors)
{
    /// <summary>A single human-readable summary safe to return to a client.</summary>
    public string Summary => Errors.Count == 0
        ? string.Empty
        : "The card design configuration is invalid: " + string.Join(", ", Errors);
}
