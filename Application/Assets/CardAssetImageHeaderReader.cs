namespace PunchedApi.Application.Assets;

/// <summary>
/// Container-header dimension detection for the raster formats this module
/// accepts. Split out from <see cref="CardAssetBinaryValidator"/> so the parsers
/// can be unit tested directly against hand-built headers.
///
/// Every reader is bounds-checked and returns null on anything unexpected: a
/// truncated or malformed header must degrade to "unsupported", never to an
/// exception or an unbounded read.
/// </summary>
public static class CardAssetImageHeaderReader
{
    /// <summary>Identifies the format and reads its declared canvas, or null when unrecognised.</summary>
    public static CardAssetFormat? Detect(ReadOnlySpan<byte> bytes)
    {
        if (IsPng(bytes)) return ReadPng(bytes);
        if (IsJpeg(bytes)) return ReadJpeg(bytes);
        if (IsGif(bytes)) return ReadGif(bytes);
        if (IsWebP(bytes)) return ReadWebP(bytes);
        return null;
    }

    // ── PNG ─────────────────────────────────────────────────

    private static bool IsPng(ReadOnlySpan<byte> b) =>
        b.Length >= 33 &&
        b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 &&
        b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A &&
        b[12] == (byte)'I' && b[13] == (byte)'H' && b[14] == (byte)'D' && b[15] == (byte)'R';

    private static CardAssetFormat? ReadPng(ReadOnlySpan<byte> b)
    {
        var width = ReadBigEndianUInt32(b, 16);
        var height = ReadBigEndianUInt32(b, 20);
        if (width == null || height == null) return null;
        return new CardAssetFormat("PNG", "image/png", "png", (int)width.Value, (int)height.Value);
    }

    // ── JPEG ────────────────────────────────────────────────

    private static bool IsJpeg(ReadOnlySpan<byte> b) => b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF;

    private static CardAssetFormat? ReadJpeg(ReadOnlySpan<byte> b)
    {
        var offset = 2;

        while (offset + 3 < b.Length)
        {
            if (b[offset] != 0xFF)
            {
                offset++;
                continue;
            }

            var marker = b[offset + 1];
            offset += 2;

            // Padding / standalone markers carry no length.
            if (marker == 0xFF || marker == 0x00 || (marker >= 0xD0 && marker <= 0xD9)) continue;

            if (offset + 1 >= b.Length) return null;
            var length = (b[offset] << 8) | b[offset + 1];
            if (length < 2) return null;

            // Start-Of-Frame markers carry the canvas. C4/C8/CC are not SOF.
            var isSof = marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
            if (isSof)
            {
                // segment: [length(2)] [precision(1)] [height(2)] [width(2)] ...
                if (offset + 7 >= b.Length) return null;
                var height = (b[offset + 3] << 8) | b[offset + 4];
                var width = (b[offset + 5] << 8) | b[offset + 6];
                return new CardAssetFormat("JPEG", "image/jpeg", "jpg", width, height);
            }

            // Start of scan: metadata is done, no SOF found.
            if (marker == 0xDA) return null;

            offset += length;
        }

        return null;
    }

    // ── GIF ─────────────────────────────────────────────────

    private static bool IsGif(ReadOnlySpan<byte> b) =>
        b.Length >= 10 &&
        b[0] == (byte)'G' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'8' &&
        (b[4] == (byte)'7' || b[4] == (byte)'9') && b[5] == (byte)'a';

    private static CardAssetFormat? ReadGif(ReadOnlySpan<byte> b)
    {
        var width = b[6] | (b[7] << 8);
        var height = b[8] | (b[9] << 8);
        return new CardAssetFormat("GIF", "image/gif", "gif", width, height);
    }

    // ── WebP ────────────────────────────────────────────────

    private static bool IsWebP(ReadOnlySpan<byte> b) =>
        b.Length >= 30 &&
        b[0] == (byte)'R' && b[1] == (byte)'I' && b[2] == (byte)'F' && b[3] == (byte)'F' &&
        b[8] == (byte)'W' && b[9] == (byte)'E' && b[10] == (byte)'B' && b[11] == (byte)'P';

    private static CardAssetFormat? ReadWebP(ReadOnlySpan<byte> b)
    {
        var chunk = System.Text.Encoding.ASCII.GetString(b.Slice(12, 4).ToArray());

        switch (chunk)
        {
            case "VP8 ": // lossy
            {
                // frame tag (3) + start code (3) then width/height as 14-bit LE.
                if (b.Length < 30) return null;
                if (b[23] != 0x9D || b[24] != 0x01 || b[25] != 0x2A) return null;
                var width = (b[26] | (b[27] << 8)) & 0x3FFF;
                var height = (b[28] | (b[29] << 8)) & 0x3FFF;
                return new CardAssetFormat("WebP", "image/webp", "webp", width, height);
            }

            case "VP8L": // lossless
            {
                if (b.Length < 25 || b[20] != 0x2F) return null;
                var bits = (uint)(b[21] | (b[22] << 8) | (b[23] << 16) | (b[24] << 24));
                var width = (int)(bits & 0x3FFF) + 1;
                var height = (int)((bits >> 14) & 0x3FFF) + 1;
                return new CardAssetFormat("WebP", "image/webp", "webp", width, height);
            }

            case "VP8X": // extended
            {
                if (b.Length < 30) return null;
                var width = (b[24] | (b[25] << 8) | (b[26] << 16)) + 1;
                var height = (b[27] | (b[28] << 8) | (b[29] << 16)) + 1;
                return new CardAssetFormat("WebP", "image/webp", "webp", width, height);
            }

            default:
                return null;
        }
    }

    private static uint? ReadBigEndianUInt32(ReadOnlySpan<byte> b, int offset)
    {
        if (offset + 4 > b.Length) return null;
        return ((uint)b[offset] << 24) | ((uint)b[offset + 1] << 16) |
               ((uint)b[offset + 2] << 8) | b[offset + 3];
    }
}
