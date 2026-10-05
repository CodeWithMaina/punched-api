namespace PunchedApi.Application.Assets;

/// <summary>
/// The single place asset URLs are constructed. Keeping it here (rather than
/// inlining strings in templates or services) means the URL shape is guaranteed
/// to match <c>CardTemplateSanitizer</c>'s allow-list, and it gives one choke
/// point if the delivery route ever changes.
/// </summary>
public static class CardAssetUrls
{
    /// <summary>Route prefix of <c>CardAssetController</c>.</summary>
    public const string RoutePrefix = "/v1/card-assets";

    /// <summary>
    /// Authenticated, tenant-scoped content URL for an asset.
    /// Shape: <c>/v1/card-assets/me/{assetId}/content</c>.
    /// </summary>
    public static string ContentPath(Guid assetId) =>
        $"{RoutePrefix}/me/{assetId:D}/content";

    /// <summary>
    /// True when <paramref name="pathOrUrl"/> is one of our own asset content
    /// URLs. Used by the renderer/sanitizer allow-list and by tests.
    /// </summary>
    public static bool IsAssetContentPath(string? pathOrUrl) =>
        !string.IsNullOrWhiteSpace(pathOrUrl) &&
        pathOrUrl.StartsWith(RoutePrefix + "/", StringComparison.Ordinal) &&
        pathOrUrl.EndsWith("/content", StringComparison.Ordinal);
}
