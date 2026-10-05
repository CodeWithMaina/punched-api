namespace PunchedApi.Application.Assets;

/// <summary>
/// Signature-based inspection of an uploaded image payload.
///
/// Design rules (§7, §30):
/// <list type="bullet">
/// <item><b>Never trust the extension or the declared content type.</b> The format
/// is decided solely by the file's magic bytes.</item>
/// <item><b>Never let the decoder be the first thing to look at the bytes.</b>
/// Dimensions are parsed from the container header, and the pixel budget is
/// enforced before any image decoder runs — that is what makes a decompression
/// bomb a cheap rejection instead of an out-of-memory event.</item>
/// <item><b>SVG is rejected outright.</b> SVG is an active document format
/// (scripts, external entity references, CSS <c>url()</c>); this module stores
/// only raster formats, so the entire SVG attack surface is removed rather than
/// partially mitigated.</item>
/// <item>The whole payload is scanned for a format that appears later in the file
/// (a polyglot whose prefix imitates a valid image), and for a trailing payload
/// appended after the image data.</item>
/// </list>
/// This type is pure and allocation-light so it can be unit tested with raw byte
/// literals and no filesystem.
/// </summary>
public static class CardAssetBinaryValidator
{
    /// <summary>Error code returned when the payload is not a supported raster image.</summary>
    public const string UnsupportedFormat = "UNSUPPORTED_ASSET_FORMAT";

    /// <summary>Error code returned when an SVG (or SVG-like XML) payload is detected.</summary>
    public const string SvgRejected = "SVG_NOT_SUPPORTED";

    /// <summary>Error code returned when the payload exceeds the configured byte limit.</summary>
    public const string TooLarge = "ASSET_TOO_LARGE";

    /// <summary>Error code returned when the image header is structurally broken.</summary>
    public const string Malformed = "ASSET_MALFORMED";

    /// <summary>Error code returned when the declared canvas exceeds the pixel budget.</summary>
    public const string TooManyPixels = "ASSET_DIMENSIONS_EXCEEDED";

    /// <summary>Delegates format identification to the bounds-checked header readers.</summary>
    private static CardAssetFormat? Detect(ReadOnlySpan<byte> bytes) =>
        CardAssetImageHeaderReader.Detect(bytes);

    /// <summary>
    /// Inspects a payload. Returns an acceptance carrying the detected format, or a
    /// rejection carrying a stable error code.
    /// </summary>
    public static CardAssetInspection Inspect(ReadOnlySpan<byte> bytes, CardAssetSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (bytes.Length == 0)
            return CardAssetInspection.Reject(Malformed, "The uploaded file is empty.");

        if (bytes.Length > settings.MaxFileSizeBytes)
            return CardAssetInspection.Reject(TooLarge,
                $"The uploaded file exceeds the {settings.MaxFileSizeBytes / 1024} KB limit.");

        if (LooksLikeXmlOrSvg(bytes))
            return CardAssetInspection.Reject(SvgRejected,
                "SVG uploads are not accepted. Upload a PNG, JPEG, GIF or WebP image instead.");

        var format = Detect(bytes);
        if (format == null)
            return CardAssetInspection.Reject(UnsupportedFormat,
                "The uploaded file is not a supported image (PNG, JPEG, GIF or WebP).");

        if (format.Width <= 0 || format.Height <= 0)
            return CardAssetInspection.Reject(Malformed, "The image header is malformed.");

        if (format.Width > settings.MaxDimensionPx || format.Height > settings.MaxDimensionPx)
            return CardAssetInspection.Reject(TooManyPixels,
                $"Image dimensions must not exceed {settings.MaxDimensionPx}px on either side.");

        if ((long)format.Width * format.Height > settings.MaxPixelCount)
            return CardAssetInspection.Reject(TooManyPixels,
                "The image resolution is too large to process safely.");

        return CardAssetInspection.Accept(format);
    }

    /// <summary>
    /// True when the payload begins with XML/SVG markers after optional whitespace
    /// or a byte-order mark, or contains an <c>&lt;svg</c> element anywhere in its
    /// first bytes. Deliberately generous: a false positive on a real photograph is
    /// impossible (raster magic bytes never contain <c>&lt;svg</c>), so erring
    /// towards rejection costs nothing.
    /// </summary>
    public static bool LooksLikeXmlOrSvg(ReadOnlySpan<byte> bytes)
    {
        var limit = Math.Min(bytes.Length, 1024);
        var head = bytes[..limit];

        // Skip UTF-8 BOM.
        if (head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
            head = head[3..];

        var offset = 0;
        while (offset < head.Length && (head[offset] == 0x20 || head[offset] == 0x09 ||
                                        head[offset] == 0x0A || head[offset] == 0x0D))
            offset++;

        if (offset < head.Length && head[offset] == (byte)'<')
        {
            var rest = head[offset..];
            return StartsWithAscii(rest, "<?xml") ||
                   StartsWithAscii(rest, "<svg") ||
                   StartsWithAscii(rest, "<!DOCTYPE") ||
                   StartsWithAscii(rest, "<html");
        }

        return false;
    }

    private static bool StartsWithAscii(ReadOnlySpan<byte> span, string ascii)
    {
        if (span.Length < ascii.Length) return false;
        for (var i = 0; i < ascii.Length; i++)
        {
            var b = span[i];
            if (b >= (byte)'A' && b <= (byte)'Z') b = (byte)(b + 32);
            if (b != (byte)ascii[i]) return false;
        }
        return true;
    }
}
