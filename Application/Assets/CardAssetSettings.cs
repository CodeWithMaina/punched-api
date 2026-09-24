namespace PunchedApi.Application.Assets;

/// <summary>
/// Limits and storage configuration for card-asset uploads. Bound from the
/// <c>CardAssets</c> configuration section so an operator can tighten the limits
/// per environment without a code change.
/// </summary>
public class CardAssetSettings
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "CardAssets";

    /// <summary>
    /// Root directory for stored payloads. Kept *outside* any web-served folder;
    /// files are only ever reached through the controller.
    /// </summary>
    public string StorageRoot { get; set; } = "App_Data/card-assets";

    /// <summary>Largest accepted upload in bytes. Default 2 MiB — a card face never needs more.</summary>
    public long MaxFileSizeBytes { get; set; } = 2 * 1024 * 1024;

    /// <summary>Largest accepted width or height in pixels.</summary>
    public int MaxDimensionPx { get; set; } = 4096;

    /// <summary>
    /// Largest accepted <c>width × height</c>. Bounds a "decompression bomb":
    /// a tiny file can declare an enormous canvas, and the guard rejects it
    /// before any decoder allocates memory.
    /// </summary>
    public long MaxPixelCount { get; set; } = 16_000_000;

    /// <summary>Largest number of live assets a single business may hold (storage-abuse guard).</summary>
    public int MaxAssetsPerBusiness { get; set; } = 200;
}
