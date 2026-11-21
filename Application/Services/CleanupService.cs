using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PunchedApi.Application.BackgroundProcessing;
using PunchedApi.Application.Notifications;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

/// <summary>
/// Background hosted service that periodically cleans up expired and stale records.
/// Runs hourly and handles expired/used QrTokens, expired/revoked RefreshTokens,
/// stale unverified verification codes, idempotency keys, expired cards and old
/// notification rows.
///
/// <para>The hourly cadence is unchanged; in scale-to-zero-friendly mode the pass is
/// folded onto the shared tick so the hourly cleanup does not wake the database on its
/// own schedule.</para>
/// </summary>
public sealed class CleanupService : BackgroundService
{
    private static readonly TimeSpan RunInterval = TimeSpan.FromHours(1);

    // How long to retain already-used/revoked records before hard-deleting them
    private static readonly TimeSpan UsedQrRetention = TimeSpan.FromDays(1);
    private static readonly TimeSpan RevokedTokenRetention = TimeSpan.FromDays(7);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CleanupService> _logger;
    private readonly NotificationWorkerOptions _notificationOptions;
    private readonly PollingProfile? _profile;
    private readonly BackgroundProcessingMetrics _metrics;

    public CleanupService(
        IServiceScopeFactory scopeFactory,
        ILogger<CleanupService> logger,
        IOptions<NotificationWorkerOptions>? notificationOptions = null,
        BackgroundProcessingOptions? backgroundProcessing = null,
        BackgroundProcessingMetrics? metrics = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _notificationOptions = notificationOptions?.Value ?? new NotificationWorkerOptions();
        _profile = backgroundProcessing?.For(BackgroundProcessingOptions.Names.Cleanup);
        _metrics = metrics ?? BackgroundProcessingMetrics.Shared;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        PeriodicWorkerLoop.RunAsync(
            BackgroundProcessingOptions.Names.Cleanup,
            _profile ?? new PollingProfile(),
            RunInterval,
            _logger,
            _metrics,
            async ct =>
            {
                var total = await RunCleanupAsync(ct);
                if (total > 0)
                {
                    _logger.LogInformation("CleanupService removed or expired {Count} record(s).", total);
                }
                return total;
            },
            stoppingToken);

    /// <summary>Runs one cleanup pass and returns how many rows were touched.</summary>
    private async Task<int> RunCleanupAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var now = DateTime.UtcNow;

        var qrDeleted = await CleanQrTokensAsync(db, now, ct);
        var refreshDeleted = await CleanRefreshTokensAsync(db, now, ct);
        var verifyCleared = await CleanVerificationCodesAsync(db, now, ct);
        var idempotencyDeleted = await CleanIdempotencyKeysAsync(db, now, ct);
        var cardsExpired = await CleanExpiredStampsAsync(ct);
        var notificationsDeleted = await CleanNotificationsAsync(
            db, now, _notificationOptions, ct);

        _logger.LogInformation(
            "Cleanup complete — QrTokens deleted: {Qr}, RefreshTokens deleted: {Refresh}, VerificationCodes cleared: {Verify}, IdempotencyKeys deleted: {Idem}, Cards expired: {Expired}, Notifications deleted: {Notifications}",
            qrDeleted, refreshDeleted, verifyCleared, idempotencyDeleted, cardsExpired, notificationsDeleted);

        return qrDeleted + refreshDeleted + verifyCleared + idempotencyDeleted + cardsExpired + notificationsDeleted;
    }

    public static async Task<int> CleanNotificationsAsync(
        ApplicationDbContext db,
        DateTime now,
        NotificationWorkerOptions options,
        CancellationToken ct)
    {
        var inboxCutoff = now.AddDays(-Math.Max(1, options.InboxRetentionDays));
        var ledgerCutoff = now.AddDays(-Math.Max(1, options.LedgerRetentionDays));
        var failedCutoff = now.AddDays(-Math.Max(1, options.FailedLedgerRetentionDays));

        var inboxDeleted = await db.Notifications
            .Where(row => row.CreatedAt < inboxCutoff && (row.IsRead || row.ArchivedAt != null))
            .ExecuteDeleteAsync(ct);
        var ledgerDeleted = await db.NotificationLogs
            .Where(row => (row.Status == "sent" && row.UpdatedAt < ledgerCutoff)
                || (row.Status == "failed" && row.UpdatedAt < failedCutoff))
            .ExecuteDeleteAsync(ct);
        return inboxDeleted + ledgerDeleted;
    }

    // ── Idempotency Keys ─────────────────────────────────────────────────────
    /// <summary>
    /// Deletes IdempotencyKey rows past their expiry (24h TTL set by the service).
    /// Deletes in bounded batches so a large backlog never blocks one long transaction.
    /// Public static for deterministic unit testing.
    /// </summary>
    public static async Task<int> CleanIdempotencyKeysAsync(ApplicationDbContext db, DateTime now, CancellationToken ct)
    {
        const int batchSize = 500;
        var totalDeleted = 0;
        while (!ct.IsCancellationRequested)
        {
            var batchIds = await db.IdempotencyKeys
                .Where(k => k.ExpiresAt < now)
                .OrderBy(k => k.ExpiresAt)
                .Take(batchSize)
                .Select(k => k.Id)
                .ToListAsync(ct);

            if (batchIds.Count == 0)
                break;

            totalDeleted += await db.IdempotencyKeys
                .Where(k => batchIds.Contains(k.Id))
                .ExecuteDeleteAsync(ct);
        }

        return totalDeleted;
    }

    // ── Stamp Expiry ─────────────────────────────────────────────────────────
    /// <summary>
    /// Expires card progress past the program's StampExpiryDays (Phase 4).
    /// Delegated to StampingMaintenanceService, which applies the guarded
    /// conditional UPDATE and notifies each affected customer (deduped).
    /// </summary>
    private async Task<int> CleanExpiredStampsAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var maintenance = scope.ServiceProvider.GetRequiredService<IStampingMaintenanceService>();
        return await maintenance.ExpireStampsAsync(ct);
    }

    // ── QR Tokens ────────────────────────────────────────────────────────────
    /// <summary>
    /// Deletes QrTokens that are either:
    ///   (a) expired and unused (no longer valid for stamping), or
    ///   (b) already used and older than the retention window.
    /// </summary>
    private static async Task<int> CleanQrTokensAsync(ApplicationDbContext db, DateTime now, CancellationToken ct)
    {
        var expiredUnused = db.QrTokens
            .Where(q => !q.IsUsed && q.ExpiresAt < now);

        var oldUsed = db.QrTokens
            .Where(q => q.IsUsed && q.ExpiresAt < now.Subtract(CleanupService.UsedQrRetention));

        db.QrTokens.RemoveRange(expiredUnused);
        db.QrTokens.RemoveRange(oldUsed);

        return await db.SaveChangesAsync(ct);
    }

    // ── Refresh Tokens ───────────────────────────────────────────────────────
    /// <summary>
    /// Deletes RefreshTokens that are either:
    ///   (a) past their expiry date, or
    ///   (b) revoked and older than the retention window.
    /// </summary>
    private static async Task<int> CleanRefreshTokensAsync(ApplicationDbContext db, DateTime now, CancellationToken ct)
    {
        var expired = db.RefreshTokens
            .Where(r => r.ExpiresAt < now);

        var oldRevoked = db.RefreshTokens
            .Where(r => r.IsRevoked && r.RevokedAt < now.Subtract(CleanupService.RevokedTokenRetention));

        db.RefreshTokens.RemoveRange(expired);
        db.RefreshTokens.RemoveRange(oldRevoked);

        return await db.SaveChangesAsync(ct);
    }

    // ── Email Verification Codes ─────────────────────────────────────────────
    /// <summary>
    /// Clears stale verification codes on UserAuth rows where:
    ///   - The code has expired, AND
    ///   - The account is still unverified (so we don't touch verified users).
    /// Does NOT delete the UserAuth row — the user may still verify later.
    /// </summary>
    private static async Task<int> CleanVerificationCodesAsync(ApplicationDbContext db, DateTime now, CancellationToken ct)
    {
        var stale = await db.UserAuths
            .Where(u => !u.IsVerified
                     && u.VerificationCode != null
                     && u.VerificationCodeExpiresAt < now)
            .ToListAsync(ct);

        foreach (var auth in stale)
        {
            auth.VerificationCode = null;
            auth.VerificationCodeExpiresAt = null;
            auth.VerificationCodeAttempts = 0;
        }

        return await db.SaveChangesAsync(ct);
    }
}
