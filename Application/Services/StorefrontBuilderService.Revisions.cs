using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

/// <summary>
/// Revision-history half of the Storefront Builder service: the helpers that turn
/// a builder operation into an immutable snapshot, and the retry that keeps
/// concurrent writers from forking the revision sequence.
///
/// <para><b>Atomicity.</b> A revision is added to the same change tracker as the
/// configuration write, so one <c>SaveChangesAsync</c> commits both or neither —
/// a failed publish can never leave an orphaned revision or a half-updated
/// published state.</para>
///
/// <para><b>Numbering.</b> The next number is <c>max + 1</c> for the business,
/// taken and written in the same unit of work. The unique
/// <c>(business_id, revision_number)</c> index is the real guard: if a concurrent
/// writer takes the number first, the whole operation is discarded and replayed
/// under the new number (see <see cref="ExecuteRevisionSafeAsync{T}"/>).</para>
/// </summary>
public partial class StorefrontBuilderService
{
    /// <summary>How many times a colliding revision number is replayed before giving up.</summary>
    private const int MaxRevisionAttempts = 3;

    /// <summary>
    /// The canonical text used to decide whether a configuration actually
    /// changed. Serialising raw inbound JSON would report a change for
    /// formatting/key-order noise; this sorts everything that carries no meaning
    /// (section/page order is re-derived from <c>order</c>, settings keys are
    /// sorted) so only real edits count.
    /// </summary>
    internal static string Normalise(StorefrontConfigDto config)
    {
        var canonical = new StorefrontConfigDto
        {
            TemplateId = config.TemplateId,
            Theme = config.Theme,
            Header = config.Header,
            Hero = config.Hero,
            Footer = config.Footer,
            Seo = config.Seo,
            Pages = (config.Pages ?? new()).OrderBy(p => p.Order).ToList(),
            Sections = (config.Sections ?? new())
                .OrderBy(s => s.Order)
                .Select(s => new StorefrontSectionDto
                {
                    Id = s.Id,
                    Key = s.Key,
                    Variant = s.Variant,
                    Enabled = s.Enabled,
                    Order = s.Order,
                    Title = s.Title,
                    Description = s.Description,
                    Settings = (s.Settings ?? new Dictionary<string, string>())
                        .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                        .ToDictionary(entry => entry.Key, entry => entry.Value),
                })
                .ToList(),
        };

        return JsonSerializer.Serialize(canonical, JsonOptions);
    }

    /// <summary>
    /// True when two configurations are the same once normalised. Used to skip
    /// meaningless revisions ("save" with nothing changed, a repeat rollback, a
    /// reset that changes nothing).
    /// </summary>
    internal static bool SameConfig(StorefrontConfigDto? a, StorefrontConfigDto? b)
    {
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;
        return string.Equals(Normalise(a), Normalise(b), StringComparison.Ordinal);
    }

    /// <summary>The highest revision number recorded for a business (0 = none).</summary>
    internal async Task<int> LatestRevisionNumberAsync(Guid businessId, CancellationToken cancellationToken = default)
        => await _context.StorefrontRevisions
            .Where(r => r.BusinessId == businessId)
            .Select(r => (int?)r.RevisionNumber)
            .MaxAsync(cancellationToken) ?? 0;

    /// <summary>Queues an immutable snapshot on the change tracker (no save).</summary>
    internal void AppendRevision(
        Guid businessId,
        int revisionNumber,
        StorefrontConfigDto config,
        StorefrontRevisionType type,
        Guid? userId,
        Guid? sourceRevisionId,
        bool isPublishedSnapshot,
        string? description)
    {
        _context.StorefrontRevisions.Add(new StorefrontRevision
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            RevisionNumber = revisionNumber,
            ConfigurationJson = JsonSerializer.Serialize(config, JsonOptions),
            RevisionType = type,
            Description = Truncate(description, 500),
            CreatedByUserId = userId,
            SourceRevisionId = sourceRevisionId,
            IsPublishedSnapshot = isPublishedSnapshot,
            CreatedAt = DateTime.UtcNow,
        });
    }

    /// <summary>
    /// Runs a builder write and replays it once if a concurrent writer took the
    /// revision number the write had reserved.
    /// </summary>
    internal Task<ApiResponse<T>> ExecuteRevisionSafeAsync<T>(Func<Task<ApiResponse<T>>> operation)
        => RunRevisionSafeAsync(_context, _logger, operation);

    /// <summary>
    /// The replay contract, shared with any service that writes revisions (the
    /// revision service restores through it too). On the final attempt the
    /// collision is reported as a conflict instead of an unhandled 500.
    /// </summary>
    internal static async Task<ApiResponse<T>> RunRevisionSafeAsync<T>(
        ApplicationDbContext context,
        ILogger logger,
        Func<Task<ApiResponse<T>>> operation)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                // Forget the failed unit of work so the replay re-reads the row
                // (and therefore re-computes the next revision number).
                context.ChangeTracker.Clear();

                if (attempt >= MaxRevisionAttempts - 1)
                {
                    logger.LogWarning(ex,
                        "Storefront revision number could not be reserved after {Attempts} attempts.", attempt + 1);
                    return ApiResponse<T>.Fail("STALE_VERSION",
                        "Someone else changed the storefront at the same moment. Reload and try again.");
                }

                logger.LogInformation(ex,
                    "Storefront revision number collided; replaying the operation (attempt {Attempt}).", attempt + 1);
            }
        }
    }

    /// <summary>
    /// Provider-agnostic unique-constraint detection: PostgreSQL reports SQLSTATE
    /// 23505, SQLite reports "UNIQUE constraint failed".
    /// </summary>
    internal static bool IsUniqueViolation(DbUpdateException exception)
    {
        for (Exception? current = exception; current != null; current = current.InnerException)
        {
            var message = current.Message;
            if (message.Contains("23505", StringComparison.Ordinal) ||
                message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("duplicate key value", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string? Truncate(string? value, int max) =>
        value == null || value.Length <= max ? value : value[..max];
}
