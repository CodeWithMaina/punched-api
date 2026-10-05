using Microsoft.EntityFrameworkCore;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

/// <summary>
/// One-time-per-boot safety net that assigns slugs to any business still
/// missing one — rows created by an older app version during a rolling
/// deploy, or legacy rows that predate the subdomain feature. Idempotent:
/// the "missing" query is a no-op once every row is provisioned. Uses the
/// same <see cref="IBusinessSlugGenerator"/> as live writes, so backfill and
/// production slugs can never disagree on normalization or reserved names.
/// </summary>
public interface IBusinessSlugBackfill
{
    /// <summary>Assigns slugs to businesses that don't have one. Returns how many were fixed.</summary>
    Task<int> EnsureSlugsAsync();
}

/// <remarks>Scoped: runs inside the startup scope right after <c>Database.MigrateAsync</c>.</remarks>
public sealed class BusinessSlugBackfill : IBusinessSlugBackfill
{
    private readonly ApplicationDbContext _context;
    private readonly IBusinessSlugGenerator _generator;
    private readonly ILogger<BusinessSlugBackfill> _logger;

    public BusinessSlugBackfill(
        ApplicationDbContext context,
        IBusinessSlugGenerator generator,
        ILogger<BusinessSlugBackfill> logger)
    {
        _context = context;
        _generator = generator;
        _logger = logger;
    }

    public async Task<int> EnsureSlugsAsync()
    {
        var missing = await LoadMissingAsync();
        if (missing.Count == 0) return 0;

        // Availability only sees committed rows, so candidates assigned in
        // THIS batch must be tracked separately or two same-named businesses
        // would both claim the base slug.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var business in missing)
        {
            business.Slug = await BusinessSlugPolicy.NextAsync(
                BusinessSlugPolicy.Slugify(business.Name),
                async candidate => seen.Contains(candidate) || !await _generator.IsAvailableAsync(candidate));
            seen.Add(business.Slug!);
        }

        try
        {
            await _context.SaveChangesAsync();
            _logger.LogInformation("Assigned subdomain slugs to {Count} businesses.", missing.Count);
            return missing.Count;
        }
        catch (DbUpdateException)
        {
            // Another replica may have provisioned some rows concurrently (the
            // unique index rejected the batch). Redo row by row so one clash
            // can't hold up the rest of the backfill.
            return await EnsureRowByRowAsync();
        }
    }

    private async Task<int> EnsureRowByRowAsync()
    {
        _context.ChangeTracker.Clear();
        var remaining = await LoadMissingAsync();
        var assigned = 0;

        foreach (var business in remaining)
        {
            business.Slug = await _generator.GenerateAsync(business.Name);
            try
            {
                await _context.SaveChangesAsync();
                assigned++;
            }
            catch (DbUpdateException)
            {
                // Last resort: random-suffixed slug; detach if even that races,
                // leaving the row for the next boot rather than failing startup.
                business.Slug = BusinessSlugPolicy.Fallback(BusinessSlugPolicy.Slugify(business.Name));
                try
                {
                    await _context.SaveChangesAsync();
                    assigned++;
                }
                catch (DbUpdateException retryEx)
                {
                    _context.Entry(business).State = EntityState.Detached;
                    _logger.LogError(retryEx,
                        "Could not assign a slug to business {BusinessId}; it will be retried on next boot.",
                        business.Id);
                }
            }
        }

        if (assigned > 0)
            _logger.LogInformation("Assigned subdomain slugs to {Count} businesses (row-by-row pass).", assigned);
        return assigned;
    }

    // Includes soft-deleted rows: undeleting a business must never reveal a
    // slug-less row. Order is stable so retries behave deterministically.
    private Task<List<Domain.Entities.Business>> LoadMissingAsync() =>
        _context.Businesses
            .IgnoreQueryFilters()
            .Where(b => b.Slug == null || b.Slug == "")
            .OrderBy(b => b.CreatedAt)
            .ThenBy(b => b.Id)
            .ToListAsync();
}