namespace PunchedApi.Application.Assets;

/// <summary>Outcome of a binary asset inspection.</summary>
/// <param name="IsValid">True when the payload is a supported, safe image.</param>
/// <param name="ErrorCode">Stable error code when invalid (never echoes input).</param>
/// <param name="Message">Human-readable, non-sensitive explanation.</param>
/// <param name="Detected">Detected format details when valid.</param>
public sealed record CardAssetInspection(
    bool IsValid,
    string? ErrorCode,
    string? Message,
    CardAssetFormat? Detected)
{
    /// <summary>Shorthand for a rejection.</summary>
    public static CardAssetInspection Reject(string code, string message) =>
        new(false, code, message, null);

    /// <summary>Shorthand for an acceptance.</summary>
    public static CardAssetInspection Accept(CardAssetFormat format) =>
        new(true, null, null, format);
}

/// <summary>
/// A detected image format. <see cref="ContentType"/> and <see cref="Extension"/>
/// are derived from the *signature*, never from client input.
/// </summary>
/// <param name="Name">Format name (e.g. "PNG").</param>
/// <param name="ContentType">Canonical MIME type.</param>
/// <param name="Extension">Canonical extension without a dot.</param>
/// <param name="Width">Decoded pixel width.</param>
/// <param name="Height">Decoded pixel height.</param>
public sealed record CardAssetFormat(string Name, string ContentType, string Extension, int Width, int Height);
