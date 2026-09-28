using Microsoft.EntityFrameworkCore;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

/// <summary>
/// Generates and validates business subdomain slugs. Availability is checked
/// against BOTH live business slugs and the superseded-slug history (so a new
/// business can never capture an address that still redirects to someone
/// else), with the database unique index as the final arbiter for races.
/// </summary>
public interface IBusinessSlugGenerator
{
    /// <summary>Deterministic name → slug normalization (no database access).</summary>
    string Slugify(string businessName);

    /// <summary>
    /// Validates owner-supplied input (case-folded first). Returns the
    /// normalized slug plus an API error code/message on rejection
    /// (SLUG_INVALID / SLUG_RESERVED).
    /// </summary>
    (bool Ok, string Slug, string Code, string Message) Validate(string? input);

    /// <summary>Slugify + first free, non-reserved candidate (suffixes on collision).</summary>
    Task<string> GenerateAsync(string businessName);

    /// <summary>
    /// True when no live business (nor history owned by a living business)
    /// currently uses the slug, optionally ignoring one business (so an owner
    /// can keep — or switch back to — their own addresses).
    /// </summary>
    Task<bool> IsAvailableAsync(string slug, Guid? excludeBusinessId = null);
}

/// <remarks>Scoped: shares the request's DbContext with the calling service.</remarks>
public sealed class BusinessSlugGenerator : IBusinessSlugGenerator
{
    private readonly ApplicationDbContext _context;

    public BusinessSlugGenerator(ApplicationDbContext context)
    {
        _context = context;
    }

    public string Slugify(string businessName) => BusinessSlugPolicy.Slugify(businessName);

    public (bool Ok, string Slug, string Code, string Message) Validate(string? input)
    {
        var slug = (input ?? string.Empty).Trim().ToLowerInvariant();

        if (!BusinessSlugPolicy.IsValidFormat(slug))
        {
            return (false, slug, "SLUG_INVALID",
                "Use lowercase letters, numbers, and hyphens only, don't start or end with a hyphen, and keep it under 63 characters.");
        }

        if (BusinessSlugPolicy.IsReserved(slug))
        {
            return (false, slug, "SLUG_RESERVED",
                "That address is reserved by Punched. Please choose another.");
        }

        return (true, slug, string.Empty, string.Empty);
    }

    public async Task<string> GenerateAsync(string businessName)
    {
        var baseSlug = BusinessSlugPolicy.Slugify(businessName);
        return await BusinessSlugPolicy.NextAsync(baseSlug, async candidate => !await IsAvailableAsync(candidate));
    }

    public async Task<bool> IsAvailableAsync(string slug, Guid? excludeBusinessId = null)
    {
        // Live slugs — the global soft-delete filter applies: a deleted
        // business frees its address.
        var liveTaken = await _context.Businesses
            .AnyAsync(b => b.Slug == slug &&
                (excludeBusinessId == null || b.Id != excludeBusinessId));

        if (liveTaken) return false;

        // History slugs — joined through Business so the soft-delete filter
        // applies too: history only blocks while its business is alive.
        var historyTaken = await
            (from h in _context.BusinessSlugHistories.AsNoTracking()
             join b in _context.Businesses.AsNoTracking() on h.BusinessId equals b.Id
             where h.Slug == slug &&
                   (excludeBusinessId == null || h.BusinessId != excludeBusinessId)
             select h.Id)
            .AnyAsync();

        return !historyTaken;
    }
}