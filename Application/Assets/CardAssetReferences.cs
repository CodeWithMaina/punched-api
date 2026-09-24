using PunchedApi.Domain.Interfaces;

namespace PunchedApi.Application.Assets;

/// <summary>
/// Shared ownership evaluation for asset ids referenced by a design config.
/// Kept as a pure function so <c>CardAssetService</c> and
/// <c>CardDesignService</c> apply byte-identical rules (one implementation of
/// "is this asset usable by this business", §2 duplicate-logic rule).
/// </summary>
public static class CardAssetReferences
{
    /// <summary>
    /// True when every non-empty requested id appears in
    /// <paramref name="ownedActiveIds"/> (assets owned by the business AND live).
    /// One error covers "not yours", "does not exist" and "deleted" so a response
    /// never confirms that another tenant's asset id is real (§20, §9).
    /// </summary>
    public static CardAssetReferenceCheck Evaluate(
        IEnumerable<Guid> requested, IEnumerable<Guid> ownedActiveIds)
    {
        var ids = (requested ?? Enumerable.Empty<Guid>())
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();

        if (ids.Count == 0) return CardAssetReferenceCheck.Valid;

        var owned = (ownedActiveIds ?? Enumerable.Empty<Guid>()).ToHashSet();

        if (ids.Any(id => !owned.Contains(id)))
            return CardAssetReferenceCheck.Invalid(
                "ASSET_NOT_AVAILABLE",
                "One or more referenced assets are unavailable. Upload the asset again and retry.");

        return CardAssetReferenceCheck.Valid;
    }
}