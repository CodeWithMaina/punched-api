using Microsoft.AspNetCore.OutputCaching;

namespace PunchedApi.Application.Services;

/// <summary>
/// Drops cached storefront responses whose content depends on a business's
/// effective module entitlements.
///
/// <para><b>Why this is needed at all.</b> The public storefront bootstrap
/// (<c>GET /v1/businesses/by-slug/{slug}/bootstrap</c>) is output-cached for 30 s
/// because it is the one call that renders a tenant's whole page — including the
/// <c>capabilities</c> map the storefront navigates by. Without eviction, a
/// module switched off in the admin console would keep appearing on storefronts
/// for the remainder of that window, even after the entitlement cache has been
/// cleared.</para>
///
/// <para><b>When to call it.</b> After ANY mutation that can change a business's
/// effective modules or customer capabilities: a module's platform availability,
/// a plan's module set, a per-business module override, a business's
/// administration status, or a subscription lifecycle action. Eviction is
/// coarse (the whole tag) and deliberately so — the entry is cheap to rebuild
/// and correctness matters more than a 30-second cache hit.</para>
/// </summary>
public interface IStorefrontCacheInvalidator
{
    /// <summary>
    /// Evicts every cached storefront bootstrap response so the next request
    /// re-resolves entitlements from the database.
    /// </summary>
    Task EvictAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class StorefrontCacheInvalidator : IStorefrontCacheInvalidator
{
    /// <summary>
    /// Output-cache tag applied to the storefront bootstrap policy in
    /// <c>Program.cs</c>. Defined once here so the policy and every eviction
    /// site can never disagree about the string.
    /// </summary>
    public const string StorefrontBootstrapTag = "storefront-bootstrap";

    private readonly IOutputCacheStore _store;
    private readonly ILogger<StorefrontCacheInvalidator> _logger;

    public StorefrontCacheInvalidator(
        IOutputCacheStore store,
        ILogger<StorefrontCacheInvalidator> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task EvictAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _store.EvictByTagAsync(StorefrontBootstrapTag, cancellationToken);
            _logger.LogInformation(
                "Evicted the '{Tag}' output cache after an entitlement change.", StorefrontBootstrapTag);
        }
        catch (Exception ex)
        {
            // The mutation itself has already succeeded and been persisted; a
            // cache-eviction failure must not turn it into an error response.
            // The entry expires on its own within the policy's 30-second window.
            _logger.LogError(
                ex,
                "Failed to evict the '{Tag}' output cache; the entry will expire on its own.",
                StorefrontBootstrapTag);
        }
    }
}
