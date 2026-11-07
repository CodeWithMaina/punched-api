using PunchedApi.Application.DTOs;

namespace PunchedApi.Application.Services;


/// <summary>
/// The prototype's six theme presets.
///
/// <para>Each preset is a named palette the owner can apply in one click. The
/// colours are the prototype's own, extracted verbatim, and applying one also
/// re-points the hero overlay and footer to the new primary — exactly what the
/// prototype's preset handler does, so the preview matches the design.</para>
/// </summary>
public static class StorefrontThemePresets
{
    public sealed record ThemePreset(
        string Id,
        string Name,
        string Primary,
        string Accent,
        string Background);

    /// <summary>The presets, in the prototype's order.</summary>
    public static readonly IReadOnlyList<ThemePreset> All = new[]
    {
        new ThemePreset("warm-studio", "Warm studio", "#1c1917", "#b45309", "#fffdf8"),
        new ThemePreset("noir-gold", "Noir gold", "#1a1712", "#cbb27a", "#faf7f0"),
        new ThemePreset("sage", "Sage", "#2f5d50", "#d98b6a", "#f8faf7"),
        new ThemePreset("navy", "Navy", "#14213d", "#e0a526", "#ffffff"),
        new ThemePreset("claret", "Claret", "#5b1e1e", "#c9a14a", "#fdf8f2"),
        new ThemePreset("mono", "Mono", "#111111", "#ff5a36", "#f4f4f2"),
    };

    public static ThemePreset? Find(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : All.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Applies a preset's palette to a theme. Only the four palette slots the
    /// prototype's preset touches are changed — the owner's typography, spacing,
    /// motion and every unrelated token are left exactly as they were.
    /// </summary>
    public static bool Apply(StorefrontThemeDto theme, string presetId)
    {
        var preset = Find(presetId);
        if (preset == null) return false;

        theme.Primary = preset.Primary;
        theme.Accent = preset.Accent;
        theme.Background = preset.Background;
        theme.ButtonBackground = preset.Primary;
        return true;
    }
}
