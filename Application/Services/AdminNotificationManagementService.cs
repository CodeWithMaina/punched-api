using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Notifications;
using PunchedApi.Application.Settings;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

/// <summary>
/// Read/write surface behind the platform's notification management module.
///
/// Every number this service returns is computed from the delivery ledger
/// (<c>notifications</c>) or from live configuration — there is no estimated or
/// fabricated metric. Recipient identifiers are masked before they leave the
/// service, and administrative mutations are written to the append-only audit
/// trail through <see cref="ISubscriptionAuditService"/>.
/// </summary>
public sealed class AdminNotificationManagementService
{
    /// <summary>Audit action vocabulary for notification administration.</summary>
    public static class AuditActions
    {
        public const string ChannelOverride = "NOTIFICATION_CHANNEL_OVERRIDE";
        public const string TemplatesReset = "NOTIFICATION_TEMPLATES_RESET";
        public const string Retry = "NOTIFICATION_RETRY";
        public const string Cancel = "NOTIFICATION_CANCEL";
        public const string ProviderTest = "NOTIFICATION_PROVIDER_TEST";
    }

    /// <summary>Cap on rows scanned for an aggregate so a huge window stays bounded.</summary>
    private const int AggregateScanLimit = 50_000;

    private readonly ApplicationDbContext _context;
    private readonly ISubscriptionAuditService _audit;
    private readonly ITemplateOverrideResolver _templates;
    private readonly HashSet<string> _registeredChannels;
    private readonly EmailSettings _email;
    private readonly SmsSettings _sms;
    private readonly VapidSettings _vapid;
    private readonly ILogger<AdminNotificationManagementService> _logger;

    public AdminNotificationManagementService(
        ApplicationDbContext context,
        ISubscriptionAuditService audit,
        ITemplateOverrideResolver templates,
        IEnumerable<INotificationChannel> channels,
        IOptions<EmailSettings> email,
        IOptions<SmsSettings> sms,
        IOptions<VapidSettings> vapid,
        ILogger<AdminNotificationManagementService> logger)
    {
        _context = context;
        _audit = audit;
        _templates = templates;
        _registeredChannels = channels.Select(channel => channel.Name).ToHashSet(StringComparer.Ordinal);
        _email = email.Value;
        _sms = sms.Value;
        _vapid = vapid.Value;
        _logger = logger;
    }

    /// <summary>Channels this deployment knows about (registered implementations).</summary>
    public IReadOnlyCollection<string> RegisteredChannels => _registeredChannels;

    // ══════════════════════════════════════════════════════════════════
    //  Log explorer
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Server-side filtered, paginated log explorer. Filters are applied in SQL;
    /// only the page's recipient identities are resolved for masking.
    /// </summary>
    public async Task<PaginatedResponse<AdminNotificationLogItemDto>> GetLogsAsync(
        string? search,
        Guid? businessId,
        string? channel,
        string? notificationType,
        string? status,
        DateTime? fromUtc,
        DateTime? toUtc,
        Guid? notificationId,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        if (page < 1) page = 1;
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = _context.NotificationLogs.AsNoTracking().AsQueryable();

        if (notificationId is { } id && id != Guid.Empty)
            query = query.Where(row => row.Id == id);
        if (businessId is { } business && business != Guid.Empty)
            query = query.Where(row => row.BusinessId == business);
        if (!string.IsNullOrWhiteSpace(channel))
            query = query.Where(row => row.Channel == channel);
        if (!string.IsNullOrWhiteSpace(notificationType))
            query = query.Where(row => row.TemplateType == notificationType);
        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(row => row.Status == status);
        if (fromUtc is { } from)
            query = query.Where(row => row.CreatedAt >= from);
        if (toUtc is { } to)
            query = query.Where(row => row.CreatedAt <= to);

        var total = await query.CountAsync(ct);

        var rows = await query
            .OrderByDescending(row => row.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(row => new LogRow(
                row.Id, row.BusinessId, row.UserId, row.Channel, row.TemplateType, row.TemplateKey,
                row.Status, row.Attempts, row.Error, row.RuleId, row.SourceJobId, row.CreatedAt,
                row.UpdatedAt, row.NextAttemptAt, row.DeliveredAt, row.DeadLetteredAt))
            .ToListAsync(ct);

        var items = await ProjectLogsAsync(rows, ct);

        if (!string.IsNullOrWhiteSpace(search))
        {
            // Recipient search runs after masking so it can never be used to
            // probe for a raw address the administrator may not read.
            var needle = search.Trim();
            items = items
                .Where(item =>
                    item.RecipientMasked.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || item.NotificationType.Contains(needle, StringComparison.OrdinalIgnoreCase)
                    || item.BusinessName?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true
                    || item.Id.ToString().Contains(needle, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return new PaginatedResponse<AdminNotificationLogItemDto>
        {
            Items = items,
            TotalCount = total,
            Page = page,
            PageSize = pageSize,
        };
    }

    /// <summary>One notification record with its attempt history and sanitized payload.</summary>
    public async Task<AdminNotificationLogDetailDto?> GetLogDetailAsync(Guid id, CancellationToken ct = default)
    {
        var row = await _context.NotificationLogs.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct);
        if (row is null) return null;

        var projected = await ProjectLogsAsync([ToLogRow(row)], ct);
        var item = projected[0];

        var detail = new AdminNotificationLogDetailDto
        {
            Id = item.Id,
            BusinessId = item.BusinessId,
            BusinessName = item.BusinessName,
            Channel = item.Channel,
            NotificationType = item.NotificationType,
            TemplateKey = item.TemplateKey,
            Status = item.Status,
            Classification = item.Classification,
            RecipientMasked = item.RecipientMasked,
            Attempts = item.Attempts,
            RetryEligible = item.RetryEligible,
            Cancellable = item.Cancellable,
            RuleId = item.RuleId,
            SourceJobId = item.SourceJobId,
            ErrorSummary = item.ErrorSummary,
            CreatedAtUtc = item.CreatedAtUtc,
            UpdatedAtUtc = item.UpdatedAtUtc,
            NextAttemptAtUtc = item.NextAttemptAtUtc,
            DeliveredAtUtc = item.DeliveredAtUtc,
            DeadLetteredAtUtc = item.DeadLetteredAtUtc,
            RecipientUserId = row.UserId,
            SanitizedError = row.Error,
            IdempotencyKey = row.IdempotencyKey,
            SanitizedPayloadJson = NotificationManagementShared.SanitizePayload(row.PayloadJson),
            DeliveryAttempts = NotificationManagementShared.ReadAttempts(row.DeliveryAttemptsJson),
        };

        var effective = await _templates.ResolveAsync(row.BusinessId, row.TemplateType, row.Channel, ct);
        if (effective is not null)
        {
            detail.TemplateSource = effective.Source;
            detail.TemplateVersion = effective.OverrideVersion;
        }

        return detail;
    }

    // ══════════════════════════════════════════════════════════════════
    //  Delivery monitoring
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Real-time operational snapshot. Queue depth, backlog age and worker health
    /// come from the ledger itself — a channel is never reported healthy merely
    /// because it is configured.
    /// </summary>
    public async Task<AdminNotificationDeliveryMonitoringDto> GetDeliveryMonitoringAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var dayAgo = now.AddHours(-24);

        var logs = _context.NotificationLogs.AsNoTracking();

        var pending = await logs.CountAsync(row => row.Status == "pending", ct);
        var processing = await logs.CountAsync(row => row.Status == "processing", ct);
        var failed = await logs.CountAsync(row => row.Status == "failed", ct);
        var deadLettered = await logs.CountAsync(row => row.DeadLetteredAt != null, ct);
        var retryScheduled = await logs.CountAsync(
            row => row.Status == "pending" && row.Attempts > 0 && row.NextAttemptAt > now, ct);

        var oldestPendingAt = await logs
            .Where(row => row.Status == "pending")
            .OrderBy(row => row.CreatedAt)
            .Select(row => (DateTime?)row.CreatedAt)
            .FirstOrDefaultAsync(ct);

        var lastSuccessfulDeliveryAt = await logs
            .Where(row => row.Status == "sent" && row.DeliveredAt != null)
            .OrderByDescending(row => row.DeliveredAt)
            .Select(row => row.DeliveredAt)
            .FirstOrDefaultAsync(ct);

        var retryActivity = await logs.CountAsync(row => row.Attempts > 1 && row.UpdatedAt >= dayAgo, ct);

        // Worker evidence: the most recent ledger transition of any kind. A worker
        // that has not touched a row recently is reported as stalled, not healthy.
        var lastWorkerActivityAt = await logs
            .OrderByDescending(row => row.UpdatedAt)
            .Select(row => (DateTime?)row.UpdatedAt)
            .FirstOrDefaultAsync(ct);

        var backlog = await BuildBacklogAsync(logs, now, ct);
        var channels = await BuildProvidersAsync(ct);

        var (workerState, workerEvidence) = DescribeWorker(lastWorkerActivityAt, pending, now);

        return new AdminNotificationDeliveryMonitoringDto
        {
            UpdatedAtUtc = now,
            QueueDepth = pending,
            Pending = pending,
            Processing = processing,
            Failed = failed,
            DeadLettered = deadLettered,
            RetryScheduled = retryScheduled,
            OldestPendingAgeSeconds = oldestPendingAt is { } oldest
                ? Math.Round((now - oldest).TotalSeconds, 1)
                : null,
            RetryActivity24h = retryActivity,
            LastSuccessfulDeliveryAtUtc = lastSuccessfulDeliveryAt,
            LastWorkerActivityAtUtc = lastWorkerActivityAt,
            WorkerState = workerState,
            WorkerEvidence = workerEvidence,
            Channels = channels,
            Backlog = backlog,
        };
    }

    private static (string State, string Evidence) DescribeWorker(
        DateTime? lastActivityAt,
        long pending,
        DateTime now)
    {
        if (lastActivityAt is null)
            return ("idle", "No delivery activity has ever been recorded on this deployment.");

        var age = now - lastActivityAt.Value;
        if (age <= TimeSpan.FromMinutes(5))
            return ("running", $"Last ledger transition {Math.Round(age.TotalSeconds)}s ago.");

        if (pending > 0)
            return ("stalled",
                $"{pending} notification(s) waiting and no ledger transition for " +
                $"{Math.Round(age.TotalMinutes)} minutes.");

        return ("idle", $"No ledger activity for {Math.Round(age.TotalMinutes)} minutes.");
    }

    private async Task<List<AdminNotificationBacklogBucketDto>> BuildBacklogAsync(
        IQueryable<NotificationLog> logs,
        DateTime now,
        CancellationToken ct)
    {
        var pendingRows = await logs
            .Where(row => row.Status == "pending")
            .Select(row => row.CreatedAt)
            .Take(AggregateScanLimit)
            .ToListAsync(ct);

        var buckets = new (string Label, TimeSpan Upper)[]
        {
            ("< 1 min", TimeSpan.FromMinutes(1)),
            ("1–5 min", TimeSpan.FromMinutes(5)),
            ("5–30 min", TimeSpan.FromMinutes(30)),
            ("30 min – 2 h", TimeSpan.FromHours(2)),
            ("> 2 h", TimeSpan.MaxValue),
        };

        var counts = new long[buckets.Length];
        foreach (var createdAt in pendingRows)
        {
            var age = now - createdAt;
            for (var i = 0; i < buckets.Length; i++)
            {
                if (age < buckets[i].Upper)
                {
                    counts[i]++;
                    break;
                }
            }
        }

        return buckets
            .Select((bucket, index) => new AdminNotificationBacklogBucketDto
            {
                Bucket = bucket.Label,
                Count = counts[index],
            })
            .ToList();
    }

    // ══════════════════════════════════════════════════════════════════
    //  Provider configuration + health
    // ══════════════════════════════════════════════════════════════════

    /// <summary>Masked provider configuration and delivery-evidence health.</summary>
    public Task<List<AdminNotificationProviderDto>> GetProvidersAsync(CancellationToken ct = default) =>
        BuildProvidersAsync(ct);

    private async Task<List<AdminNotificationProviderDto>> BuildProvidersAsync(CancellationToken ct)
    {
        var dayAgo = DateTime.UtcNow.AddHours(-24);

        return
        [
            await BuildEmailProviderAsync(dayAgo, ct),
            await BuildSmsProviderAsync(dayAgo, ct),
            await BuildPushProviderAsync(dayAgo, ct),
            await BuildInAppProviderAsync(dayAgo, ct),
        ];
    }

    private async Task<AdminNotificationProviderDto> BuildEmailProviderAsync(DateTime dayAgo, CancellationToken ct)
    {
        var provider = new AdminNotificationProviderDto
        {
            Channel = NotificationChannel.Email,
            Registered = _registeredChannels.Contains(NotificationChannel.Email),
            Configured = _email.Enabled && !string.IsNullOrWhiteSpace(_email.FromAddress),
            SupportsConnectionTest = true,
            Settings = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["host"] = _email.Host,
                ["port"] = _email.Port.ToString(),
                ["fromAddress"] = _email.FromAddress,
                ["fromName"] = _email.FromName,
                ["enabled"] = _email.Enabled.ToString(),
                ["timeoutSeconds"] = _email.TimeoutSeconds.ToString(),
            },
            // Credentials are reported as present/absent only — never their value.
            MaskedCredentials = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["username"] = Mask(_email.Username),
                ["password"] = Mask(_email.Password),
            },
        };

        provider.Status = !provider.Registered
            ? "not_registered"
            : !_email.Enabled ? "disabled"
            : string.IsNullOrWhiteSpace(_email.FromAddress) ? "incomplete"
            : string.IsNullOrWhiteSpace(_email.Username) || string.IsNullOrWhiteSpace(_email.Password)
                ? "unauthenticated"
            : "configured";

        provider.Health = await BuildHealthAsync(NotificationChannel.Email, dayAgo, provider.Configured, ct);
        return provider;
    }

    private async Task<AdminNotificationProviderDto> BuildSmsProviderAsync(DateTime dayAgo, CancellationToken ct)
    {
        var provider = new AdminNotificationProviderDto
        {
            Channel = NotificationChannel.Sms,
            Registered = _registeredChannels.Contains(NotificationChannel.Sms),
            Configured = _sms.Enabled
                && !string.IsNullOrWhiteSpace(_sms.Username)
                && !string.IsNullOrWhiteSpace(_sms.ApiKey),
            SupportsConnectionTest = true,
            Settings = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["provider"] = _sms.Provider,
                ["senderId"] = _sms.SenderId,
                ["enabled"] = _sms.Enabled.ToString(),
            },
            MaskedCredentials = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["username"] = Mask(_sms.Username),
                ["apiKey"] = Mask(_sms.ApiKey),
            },
        };

        provider.Status = !provider.Registered
            ? "not_registered"
            : !_sms.Enabled ? "disabled"
            : string.IsNullOrWhiteSpace(_sms.Username) || string.IsNullOrWhiteSpace(_sms.ApiKey)
                ? "incomplete"
            : "configured";

        provider.Health = await BuildHealthAsync(NotificationChannel.Sms, dayAgo, provider.Configured, ct);
        return provider;
    }

    /// <summary>Reports a credential's presence without revealing it.</summary>
    private static string Mask(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) return "not_set";
        if (secret!.Length <= 4) return "••••";
        return $"••••{secret[^4..]}";
    }

    private async Task<AdminNotificationProviderDto> BuildPushProviderAsync(DateTime dayAgo, CancellationToken ct)
    {
        var provider = new AdminNotificationProviderDto
        {
            Channel = NotificationChannel.Push,
            Registered = _registeredChannels.Contains(NotificationChannel.Push),
            Configured = _vapid.Enabled
                && !string.IsNullOrWhiteSpace(_vapid.PublicKey)
                && !string.IsNullOrWhiteSpace(_vapid.PrivateKey),
            SupportsConnectionTest = true,
            Settings = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["publicKey"] = _vapid.PublicKey,
                ["subject"] = _vapid.Subject,
                ["enabled"] = _vapid.Enabled.ToString(),
            },
            MaskedCredentials = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["privateKey"] = Mask(_vapid.PrivateKey),
            },
        };

        provider.Status = !provider.Registered
            ? "not_registered"
            : !_vapid.Enabled ? "disabled"
            : string.IsNullOrWhiteSpace(_vapid.PublicKey) || string.IsNullOrWhiteSpace(_vapid.PrivateKey)
                ? "incomplete"
            : "configured";

        var activeDevices = await _context.PushDevices.AsNoTracking().CountAsync(device => device.IsActive, ct);
        provider.Settings["activeDevices"] = activeDevices.ToString();

        provider.Health = await BuildHealthAsync(NotificationChannel.Push, dayAgo, provider.Configured, ct);
        return provider;
    }

    private async Task<AdminNotificationProviderDto> BuildInAppProviderAsync(DateTime dayAgo, CancellationToken ct)
    {
        var provider = new AdminNotificationProviderDto
        {
            Channel = NotificationChannel.InApp,
            Registered = true,
            Configured = true,
            SupportsConnectionTest = false,
            Status = "configured",
            Settings = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["delivery"] = "inbox_insert",
            },
        };

        var unread = await _context.Notifications
            .AsNoTracking()
            .CountAsync(row => !row.IsRead && row.ArchivedAt == null, ct);
        provider.Settings["unreadInboxRows"] = unread.ToString();

        provider.Health = await BuildHealthAsync(NotificationChannel.InApp, dayAgo, configured: true, ct);
        return provider;
    }

    /// <summary>
    /// Health is derived from delivery evidence over the last 24 hours. A channel
    /// with configuration but no evidence is reported as "unknown", never "healthy".
    /// </summary>
    private async Task<AdminNotificationProviderHealthDto> BuildHealthAsync(
        string channel,
        DateTime dayAgo,
        bool configured,
        CancellationToken ct)
    {
        var recent = await _context.NotificationLogs
            .AsNoTracking()
            .Where(row => row.Channel == channel && row.UpdatedAt >= dayAgo)
            .Select(row => new { row.Status, row.DeliveredAt })
            .Take(AggregateScanLimit)
            .ToListAsync(ct);

        var attempts = recent.Count;
        var failures = recent.Count(row => row.Status == "failed");
        var sent = recent.Count(row => row.Status == "sent");
        var lastSuccess = recent
            .Where(row => row.DeliveredAt.HasValue)
            .Select(row => row.DeliveredAt!.Value)
            .DefaultIfEmpty()
            .Max();

        var health = new AdminNotificationProviderHealthDto
        {
            RecentAttempts = attempts,
            RecentFailures = failures,
            LastSuccessfulDeliveryAtUtc = sent > 0 ? lastSuccess : null,
        };

        if (!configured)
        {
            health.State = "unconfigured";
            health.Evidence = "This deployment has not configured the channel, so no delivery is attempted.";
            return health;
        }

        if (attempts == 0)
        {
            health.State = "unknown";
            health.Evidence = "No delivery activity in the last 24 hours — health cannot be confirmed from configuration alone.";
            return health;
        }

        var successRate = sent / (double)attempts;
        if (successRate >= 0.95)
        {
            health.State = "healthy";
            health.Evidence = $"{sent}/{attempts} recent attempts delivered successfully.";
        }
        else if (successRate >= 0.7)
        {
            health.State = "degraded";
            health.Evidence = $"{failures} of {attempts} recent attempts failed.";
        }
        else
        {
            health.State = "unhealthy";
            health.Evidence = $"{failures} of {attempts} recent attempts failed — check the provider.";
        }

        return health;
    }

    /// <summary>
    /// Runs a real connectivity check for one channel. Nothing is sent to a
    /// recipient; the check only proves the deployment can reach the provider.
    /// </summary>
    public async Task<AdminNotificationProviderTestResultDto> TestProviderAsync(
        string channel,
        Guid? actorUserId,
        CancellationToken ct = default)
    {
        var result = new AdminNotificationProviderTestResultDto
        {
            Channel = channel,
            CheckedAtUtc = DateTime.UtcNow,
        };

        switch (channel)
        {
            case NotificationChannel.Email:
                if (!_email.Enabled)
                {
                    result.Message = "Email delivery is disabled in this deployment's configuration.";
                    break;
                }

                try
                {
                    using var client = new MailKit.Net.Smtp.SmtpClient();
                    client.Timeout = Math.Max(5, _email.TimeoutSeconds) * 1000;
                    await client.ConnectAsync(_email.Host, _email.Port, MailKit.Security.SecureSocketOptions.Auto, ct);
                    if (!string.IsNullOrWhiteSpace(_email.Username))
                        await client.AuthenticateAsync(_email.Username, _email.Password, ct);

                    await client.DisconnectAsync(true, ct);
                    result.Succeeded = true;
                    result.Message = $"Connected to {_email.Host}:{_email.Port} and authenticated successfully.";
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    result.Message = $"SMTP check failed ({ex.GetType().Name}).";
                    _logger.LogWarning(ex, "Admin SMTP provider test failed for host {Host}.", _email.Host);
                }

                break;

            case NotificationChannel.Sms:
                if (!_sms.Enabled)
                {
                    result.Message = "SMS delivery is disabled in this deployment's configuration.";
                    break;
                }

                if (string.IsNullOrWhiteSpace(_sms.Username) || string.IsNullOrWhiteSpace(_sms.ApiKey))
                {
                    result.Message = "SMS credentials are incomplete.";
                    break;
                }

                result.Succeeded = true;
                result.Message = $"Provider '{_sms.Provider}' is configured with sender id '{_sms.SenderId}'. " +
                                 "Credentials are verified on the first real send.";
                break;

            case NotificationChannel.Push:
                if (!_vapid.Enabled || string.IsNullOrWhiteSpace(_vapid.PublicKey)
                    || string.IsNullOrWhiteSpace(_vapid.PrivateKey))
                {
                    result.Message = "Web push is not fully configured (VAPID key pair missing).";
                    break;
                }

                var devices = await _context.PushDevices
                    .AsNoTracking()
                    .CountAsync(device => device.IsActive, ct);
                result.Succeeded = true;
                result.Message = $"VAPID key pair present with {devices} active device subscription(s).";
                break;

            case NotificationChannel.InApp:
                result.Succeeded = true;
                result.Message = "In-app delivery writes to the application inbox and needs no provider.";
                break;

            default:
                result.Message = $"Unknown channel '{channel}'.";
                break;
        }

        await _audit.RecordAsync(
            AuditActions.ProviderTest,
            actorUserId,
            targetBusinessId: null,
            targetPlanId: null,
            payloadJson: $"{{\"channel\":\"{channel}\",\"succeeded\":{result.Succeeded.ToString().ToLowerInvariant()}}}",
            reason: result.Message);

        return result;
    }

    // ══════════════════════════════════════════════════════════════════
    //  Global (platform) templates
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// The platform's code-defined templates with their allowed variables and the
    /// number of business overrides currently in force. Protected (SECURITY) types
    /// are flagged so the console can refuse to offer customisation.
    /// </summary>
    public async Task<List<AdminNotificationTemplateDto>> GetGlobalTemplatesAsync(CancellationToken ct = default)
    {
        var overrideCounts = await _context.NotificationTemplateOverrides
            .AsNoTracking()
            .GroupBy(row => new { row.Type, row.Channel })
            .Select(group => new { group.Key.Type, group.Key.Channel, Count = group.Count() })
            .ToListAsync(ct);

        var counts = overrideCounts.ToDictionary(
            row => (row.Type, row.Channel),
            row => row.Count);

        var templates = new List<AdminNotificationTemplateDto>();
        foreach (var definition in NotificationTypes.All)
        {
            foreach (var channel in definition.DefaultChannels)
            {
                if (!NotificationTemplates.TryGet(definition.Name, channel, out var template))
                    continue;

                counts.TryGetValue((definition.Name, channel), out var overrideCount);

                templates.Add(new AdminNotificationTemplateDto
                {
                    Type = definition.Name,
                    Category = definition.Category,
                    Channel = channel,
                    Subject = template.Subject,
                    Title = template.Title,
                    Body = template.Body,
                    DefaultChannels = definition.DefaultChannels.ToList(),
                    Protected = definition.IsSecurity,
                    AvailableVariables = NotificationManagementShared.AvailableTokens(definition),
                    OverrideCount = overrideCount,
                });
            }
        }

        return templates.OrderBy(t => t.Category, StringComparer.Ordinal)
            .ThenBy(t => t.Type, StringComparer.Ordinal)
            .ThenBy(t => t.Channel, StringComparer.Ordinal)
            .ToList();
    }

    // ══════════════════════════════════════════════════════════════════
    //  Per-business notification management
    // ══════════════════════════════════════════════════════════════════

    /// <summary>Paginated businesses with their notification posture.</summary>
    public async Task<PaginatedResponse<AdminNotificationBusinessSummaryDto>> GetBusinessesAsync(
        string? search,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        if (page < 1) page = 1;
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = _context.Businesses.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var needle = search.Trim().ToLowerInvariant();
            query = query.Where(business => business.Name.ToLower().Contains(needle));
        }

        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderBy(business => business.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(business => new { business.Id, business.Name })
            .ToListAsync(ct);

        var items = new List<AdminNotificationBusinessSummaryDto>(rows.Count);
        foreach (var row in rows)
        {
            items.Add(await BuildBusinessSummaryAsync(row.Id, row.Name, ct));
        }

        return new PaginatedResponse<AdminNotificationBusinessSummaryDto>
        {
            Items = items,
            TotalCount = total,
            Page = page,
            PageSize = pageSize,
        };
    }
    // ══════════════════════════════════════════════════════════════════
    //  Failed-notification operations
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Requeues a retry-exhausted notification. The ledger row *is* the outbox
    /// record, so re-pending it cannot duplicate a delivery: the worker claims
    /// rows by id and only ever delivers a row once per attempt.
    /// </summary>
    public async Task<AdminNotificationActionResponse> RetryAsync(
        Guid id,
        Guid? actorUserId,
        CancellationToken ct = default)
    {
        var row = await _context.NotificationLogs.FirstOrDefaultAsync(item => item.Id == id, ct);
        if (row is null)
        {
            return new AdminNotificationActionResponse
            {
                Id = id,
                Accepted = false,
                Code = "NOT_FOUND",
                Message = "Notification not found.",
            };
        }

        if (!NotificationManagementShared.IsRetryEligible(row.Status, row.Error))
        {
            return new AdminNotificationActionResponse
            {
                Id = id,
                Accepted = false,
                Code = "NOT_RETRYABLE",
                Message = "Only retry-exhausted notifications can be retried. " +
                          "Permanent failures must be fixed at the source.",
            };
        }

        var previousError = row.Error;
        var now = DateTime.UtcNow;
        row.Status = "pending";
        row.Attempts = 0;
        row.Error = null;
        row.NextAttemptAt = now;
        row.UpdatedAt = now;
        row.DeadLetteredAt = null;
        row.DeadLetterReason = null;
        await _context.SaveChangesAsync(ct);

        await _audit.RecordAsync(
            AuditActions.Retry,
            actorUserId,
            row.BusinessId,
            targetPlanId: null,
            payloadJson: System.Text.Json.JsonSerializer.Serialize(new
            {
                notificationId = id,
                channel = row.Channel,
                notificationType = row.TemplateType,
                previousError,
            }),
            reason: "Administrator requeued a retry-exhausted notification.");

        _logger.LogInformation("Administrator {ActorId} requeued notification {NotificationId}.", actorUserId, id);

        return new AdminNotificationActionResponse
        {
            Id = id,
            Accepted = true,
            Code = "ACCEPTED",
            Message = "Notification requeued for delivery.",
        };
    }

    /// <summary>Cancels a notification that has not yet been claimed by the worker.</summary>
    public async Task<AdminNotificationActionResponse> CancelAsync(
        Guid id,
        Guid? actorUserId,
        string? reason,
        CancellationToken ct = default)
    {
        var row = await _context.NotificationLogs.FirstOrDefaultAsync(item => item.Id == id, ct);
        if (row is null)
        {
            return new AdminNotificationActionResponse
            {
                Id = id,
                Accepted = false,
                Code = "NOT_FOUND",
                Message = "Notification not found.",
            };
        }

        if (!NotificationManagementShared.IsCancellable(row.Status))
        {
            return new AdminNotificationActionResponse
            {
                Id = id,
                Accepted = false,
                Code = "NOT_CANCELLABLE",
                Message = $"A notification in status '{row.Status}' cannot be cancelled.",
            };
        }

        var now = DateTime.UtcNow;
        row.Status = "cancelled";
        row.Error = "cancelled:admin";
        row.UpdatedAt = now;
        await _context.SaveChangesAsync(ct);

        await _audit.RecordAsync(
            AuditActions.Cancel,
            actorUserId,
            row.BusinessId,
            targetPlanId: null,
            payloadJson: System.Text.Json.JsonSerializer.Serialize(new
            {
                notificationId = id,
                channel = row.Channel,
                notificationType = row.TemplateType,
            }),
            reason: reason ?? "Notification cancelled by administrator.");

        _logger.LogInformation("Administrator {ActorId} cancelled notification {NotificationId}.", actorUserId, id);

        return new AdminNotificationActionResponse
        {
            Id = id,
            Accepted = true,
            Code = "CANCELLED",
            Message = "Notification cancelled before delivery.",
        };
    }

    // ══════════════════════════════════════════════════════════════════
    //  Analytics
    // ══════════════════════════════════════════════════════════════════

    /// <summary>Delivery analytics for a date range, computed entirely from the ledger.</summary>
    public async Task<AdminNotificationAnalyticsDto> GetAnalyticsAsync(
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken ct = default)
    {
        if (toUtc <= fromUtc) toUtc = fromUtc.AddDays(1);

        var rows = await _context.NotificationLogs
            .AsNoTracking()
            .Where(row => row.CreatedAt >= fromUtc && row.CreatedAt <= toUtc)
            .Select(row => new AnalyticsRow(
                row.BusinessId, row.Channel, row.TemplateType, row.Status,
                row.CreatedAt, row.DeliveredAt, row.PayloadJson))
            .Take(AggregateScanLimit)
            .ToListAsync(ct);

        var sent = rows.Count(row => row.Status == "sent");
        var failed = rows.Count(row => row.Status == "failed");
        var pending = rows.Count(row => row.Status == "pending");
        var processing = rows.Count(row => row.Status == "processing");

        var businessIds = rows
            .Where(row => row.BusinessId.HasValue)
            .Select(row => row.BusinessId!.Value)
            .Distinct()
            .ToList();

        var businessNames = businessIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _context.Businesses
                .AsNoTracking()
                .Where(business => businessIds.Contains(business.Id))
                .Select(business => new { business.Id, business.Name })
                .ToDictionaryAsync(entry => entry.Id, entry => entry.Name, ct);

        var byChannel = rows
            .GroupBy(row => row.Channel, StringComparer.Ordinal)
            .Select(group => ToCount(group.Key, group.Key, group.Select(item => item.Status)))
            .OrderByDescending(entry => entry.Total)
            .ToList();

        var byType = rows
            .GroupBy(row => row.TemplateType, StringComparer.Ordinal)
            .Select(group => ToCount(group.Key, group.Key, group.Select(item => item.Status)))
            .OrderByDescending(entry => entry.Total)
            .ToList();

        var byBusiness = rows
            .Where(row => row.BusinessId.HasValue)
            .GroupBy(row => row.BusinessId!.Value)
            .Select(group => ToCount(
                group.Key.ToString(),
                businessNames.TryGetValue(group.Key, out var name) ? name : group.Key.ToString(),
                group.Select(item => item.Status)))
            .OrderByDescending(entry => entry.Total)
            .ToList();

        var providerPerformance = rows
            .GroupBy(row => row.Channel, StringComparer.Ordinal)
            .Select(BuildProviderPerformance)
            .OrderByDescending(entry => entry.Total)
            .ToList();

        var dailyTrend = rows
            .GroupBy(row => row.CreatedAt.Date)
            .OrderBy(group => group.Key)
            .Select(group => new AdminNotificationAnalyticsTrendPointDto
            {
                Date = group.Key.ToString("yyyy-MM-dd"),
                Sent = group.Count(row => row.Status == "sent"),
                Failed = group.Count(row => row.Status == "failed"),
                Pending = group.Count(row => row.Status == "pending"),
            })
            .ToList();

        // SMS segment usage: estimated from the rendered body actually queued, so
        // it reflects real sends rather than a notional template length.
        long smsSegments = 0;
        foreach (var row in rows.Where(row => row.Channel == NotificationChannel.Sms))
        {
            var body = ExtractRenderedBody(row.PayloadJson);
            if (body is null) continue;
            smsSegments += NotificationManagementShared.EstimateSmsSegments(body).Segments;
        }

        return new AdminNotificationAnalyticsDto
        {
            FromUtc = fromUtc,
            ToUtc = toUtc,
            UpdatedAtUtc = DateTime.UtcNow,
            TotalNotifications = rows.Count,
            Sent = sent,
            Failed = failed,
            Pending = pending,
            Processing = processing,
            DeliverySuccessRatePercent = NotificationManagementShared.Percent(sent, sent + failed),
            FailureRatePercent = NotificationManagementShared.Percent(failed, sent + failed),
            TotalSmsSegments = smsSegments,
            ByChannel = byChannel,
            ByType = byType,
            ByBusiness = byBusiness,
            ProviderPerformance = providerPerformance,
            DailyTrend = dailyTrend,
        };
    }

    private static AdminNotificationProviderPerformanceDto BuildProviderPerformance(
        IGrouping<string, AnalyticsRow> group)
    {
        var groupSent = group.Count(row => row.Status == "sent");
        var groupFailed = group.Count(row => row.Status == "failed");
        var deliverySeconds = group
            .Where(row => row.DeliveredAt.HasValue)
            .Select(row => (row.DeliveredAt!.Value - row.CreatedAt).TotalSeconds)
            .Where(seconds => seconds >= 0)
            .ToList();

        return new AdminNotificationProviderPerformanceDto
        {
            Channel = group.Key,
            Total = group.Count(),
            Sent = groupSent,
            Failed = groupFailed,
            AverageDeliverySeconds = deliverySeconds.Count == 0 ? null : Math.Round(deliverySeconds.Average(), 1),
            SuccessRatePercent = NotificationManagementShared.Percent(groupSent, groupSent + groupFailed),
        };
    }

    private static string? ExtractRenderedBody(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(payloadJson);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
            return document.RootElement.TryGetProperty("renderedBody", out var body)
                && body.ValueKind == System.Text.Json.JsonValueKind.String
                    ? body.GetString()
                    : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>A ledger row flattened for analytics aggregation.</summary>
    private readonly record struct AnalyticsRow(
        Guid? BusinessId,
        string Channel,
        string TemplateType,
        string Status,
        DateTime CreatedAt,
        DateTime? DeliveredAt,
        string? PayloadJson);

    // ══════════════════════════════════════════════════════════════════
    //  Administrative mutations (audited)
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Enables or disables a delivery channel for one business by writing (or
    /// clearing) the business-level kill-switch rows the preference resolver
    /// already honours. The change is explicit and audited.
    /// </summary>
    public async Task<AdminNotificationBusinessDetailDto?> SetBusinessChannelAsync(
        Guid businessId,
        string channel,
        bool enabled,
        Guid? actorUserId,
        string? reason,
        CancellationToken ct = default)
    {
        var business = await _context.Businesses
            .AsNoTracking()
            .Where(entity => entity.Id == businessId)
            .Select(entity => new { entity.Id, entity.Name })
            .FirstOrDefaultAsync(ct);

        if (business is null) return null;

        var existing = await _context.NotificationPreferences
            .Where(row => row.BusinessId == businessId && row.UserId == null && row.Channel == channel)
            .ToListAsync(ct);

        var before = existing.ToDictionary(row => row.Category, row => row.Enabled);

        if (enabled)
        {
            // Enabling clears the kill-switch, returning the channel to the
            // system default rather than force-opening it (a business can only
            // ever close a channel — R1).
            _context.NotificationPreferences.RemoveRange(existing);
        }
        else
        {
            var now = DateTime.UtcNow;
            foreach (var category in NotificationDefaults.AllCategories)
            {
                var row = existing.FirstOrDefault(entry => entry.Category == category);
                if (row is null)
                {
                    _context.NotificationPreferences.Add(new NotificationPreference
                    {
                        Id = Guid.NewGuid(),
                        BusinessId = businessId,
                        UserId = null,
                        Category = category,
                        Channel = channel,
                        Enabled = false,
                        UpdatedAt = now,
                        CreatedAt = now,
                    });
                }
                else
                {
                    row.Enabled = false;
                    row.UpdatedAt = now;
                }
            }
        }

        await _context.SaveChangesAsync(ct);

        await _audit.RecordAsync(
            AuditActions.ChannelOverride,
            actorUserId,
            businessId,
            targetPlanId: null,
            payloadJson: System.Text.Json.JsonSerializer.Serialize(new
            {
                channel,
                enabled,
                before = before.Select(entry => new { category = entry.Key, enabled = entry.Value }),
            }),
            reason: reason ?? (enabled ? "Channel enabled by administrator." : "Channel disabled by administrator."));

        _logger.LogInformation(
            "Administrator {ActorId} set notification channel {Channel} to {Enabled} for business {BusinessId}.",
            actorUserId, channel, enabled, businessId);

        return await GetBusinessDetailAsync(businessId, ct);
    }

    /// <summary>Removes every business template override, reverting to platform defaults.</summary>
    public async Task<AdminNotificationBusinessDetailDto?> ResetBusinessTemplatesAsync(
        Guid businessId,
        Guid? actorUserId,
        CancellationToken ct = default)
    {
        var exists = await _context.Businesses.AsNoTracking().AnyAsync(entity => entity.Id == businessId, ct);
        if (!exists) return null;

        var overrides = await _context.NotificationTemplateOverrides
            .Where(row => row.BusinessId == businessId)
            .ToListAsync(ct);

        var removed = overrides
            .Select(row => new { row.Type, row.Channel, row.Version })
            .ToList();

        _context.NotificationTemplateOverrides.RemoveRange(overrides);
        await _context.SaveChangesAsync(ct);

        await _audit.RecordAsync(
            AuditActions.TemplatesReset,
            actorUserId,
            businessId,
            targetPlanId: null,
            payloadJson: System.Text.Json.JsonSerializer.Serialize(new { removed }),
            reason: $"{removed.Count} template override(s) reset to platform defaults.");

        return await GetBusinessDetailAsync(businessId, ct);
    }

    /// <summary>The business-level (category × channel) matrix, resolved and flagged for admin overrides.</summary>
    private async Task<List<AdminNotificationBusinessPreferenceDto>> BuildBusinessPreferencesAsync(
        Guid businessId,
        CancellationToken ct)
    {
        var rows = await _context.NotificationPreferences
            .AsNoTracking()
            .Where(row => row.BusinessId == businessId && row.UserId == null)
            .Select(row => new { row.Category, row.Channel, row.Enabled })
            .ToListAsync(ct);

        var lookup = rows.ToDictionary(
            row => (row.Category, row.Channel),
            row => row.Enabled);

        var preferences = new List<AdminNotificationBusinessPreferenceDto>();
        foreach (var category in NotificationDefaults.AllCategories)
        {
            foreach (var channel in NotificationDefaults.AllChannels)
            {
                var available = NotificationManagementShared.IsChannelAvailable(channel, _registeredChannels);
                var hasOverride = lookup.TryGetValue((category, channel), out var overridden);
                var enabled = hasOverride ? overridden : NotificationDefaults.IsEnabled(category, channel);

                preferences.Add(new AdminNotificationBusinessPreferenceDto
                {
                    Category = category,
                    Channel = channel,
                    Enabled = enabled,
                    AdminOverride = hasOverride && !overridden,
                });
            }
        }

        return preferences;
    }

    /// <summary>Per-channel, per-type and daily aggregates for one business.</summary>
    private async Task<AdminNotificationBusinessStatisticsDto> BuildBusinessStatisticsAsync(
        Guid businessId,
        CancellationToken ct)
    {
        var from = DateTime.UtcNow.AddDays(-30).Date;

        var rows = await _context.NotificationLogs
            .AsNoTracking()
            .Where(row => row.BusinessId == businessId && row.CreatedAt >= from)
            .Select(row => new { row.Channel, row.TemplateType, row.Status, row.CreatedAt })
            .Take(AggregateScanLimit)
            .ToListAsync(ct);

        var byChannel = rows
            .GroupBy(row => row.Channel, StringComparer.Ordinal)
            .Select(group => ToCount(group.Key, group.Key, group.Select(item => item.Status)))
            .OrderByDescending(entry => entry.Total)
            .ToList();

        var byType = rows
            .GroupBy(row => row.TemplateType, StringComparer.Ordinal)
            .Select(group => ToCount(group.Key, group.Key, group.Select(item => item.Status)))
            .OrderByDescending(entry => entry.Total)
            .ToList();

        var dailyTrend = rows
            .GroupBy(row => row.CreatedAt.Date)
            .OrderBy(group => group.Key)
            .Select(group => new AdminNotificationAnalyticsTrendPointDto
            {
                Date = group.Key.ToString("yyyy-MM-dd"),
                Sent = group.Count(row => row.Status == "sent"),
                Failed = group.Count(row => row.Status == "failed"),
                Pending = group.Count(row => row.Status == "pending"),
            })
            .ToList();

        return new AdminNotificationBusinessStatisticsDto
        {
            ByChannel = byChannel,
            ByType = byType,
            DailyTrend = dailyTrend,
        };
    }

    private static AdminNotificationAnalyticsCountDto ToCount(
        string key,
        string label,
        IEnumerable<string> statuses)
    {
        var list = statuses as IReadOnlyCollection<string> ?? statuses.ToList();
        var sent = list.Count(status => status == "sent");
        var failed = list.Count(status => status == "failed");
        var pending = list.Count(status => status == "pending");

        return new AdminNotificationAnalyticsCountDto
        {
            Key = key,
            Label = NotificationManagementShared.Humanize(label),
            Total = list.Count,
            Sent = sent,
            Failed = failed,
            Pending = pending,
            SuccessRatePercent = NotificationManagementShared.Percent(sent, sent + failed),
        };
    }

    /// <summary>Full notification posture for one business, or null when it does not exist.</summary>
    public async Task<AdminNotificationBusinessDetailDto?> GetBusinessDetailAsync(
        Guid businessId,
        CancellationToken ct = default)
    {
        var business = await _context.Businesses
            .AsNoTracking()
            .Where(entity => entity.Id == businessId)
            .Select(entity => new { entity.Id, entity.Name })
            .FirstOrDefaultAsync(ct);

        if (business is null) return null;

        var summary = await BuildBusinessSummaryAsync(business.Id, business.Name, ct);

        var suppressedChannels = summary.ChannelOverrides
            .Where(entry => !entry.Enabled)
            .Select(entry => entry.Channel)
            .ToHashSet(StringComparer.Ordinal);

        var enabledChannels = NotificationDefaults.AllChannels
            .Where(channel => NotificationManagementShared.IsChannelAvailable(channel, _registeredChannels))
            .Where(channel => !suppressedChannels.Contains(channel))
            .ToList();

        var preferences = await BuildBusinessPreferencesAsync(businessId, ct);

        var templates = await _context.NotificationTemplateOverrides
            .AsNoTracking()
            .Where(row => row.BusinessId == businessId)
            .OrderByDescending(row => row.UpdatedAt ?? row.CreatedAt)
            .Select(row => new AdminNotificationBusinessTemplateDto
            {
                Id = row.Id,
                Type = row.Type,
                Channel = row.Channel,
                Version = row.Version,
                UpdatedBy = row.UpdatedBy,
                UpdatedAtUtc = row.UpdatedAt ?? row.CreatedAt,
            })
            .ToListAsync(ct);

        var rules = await _context.AutomationRules
            .AsNoTracking()
            .Where(row => row.BusinessId == businessId)
            .OrderByDescending(row => row.UpdatedAt)
            .Select(row => new AdminNotificationBusinessRuleDto
            {
                Id = row.Id,
                Name = row.Name,
                Trigger = row.Trigger,
                Enabled = row.Enabled,
                UpdatedAtUtc = row.UpdatedAt,
            })
            .ToListAsync(ct);

        var recent = await ProjectLogsAsync(
            await _context.NotificationLogs
                .AsNoTracking()
                .Where(row => row.BusinessId == businessId)
                .OrderByDescending(row => row.CreatedAt)
                .Take(15)
                .Select(row => new LogRow(
                    row.Id, row.BusinessId, row.UserId, row.Channel, row.TemplateType, row.TemplateKey,
                    row.Status, row.Attempts, row.Error, row.RuleId, row.SourceJobId, row.CreatedAt,
                    row.UpdatedAt, row.NextAttemptAt, row.DeliveredAt, row.DeadLetteredAt))
                .ToListAsync(ct),
            ct);

        var failures = await ProjectLogsAsync(
            await _context.NotificationLogs
                .AsNoTracking()
                .Where(row => row.BusinessId == businessId && row.Status == "failed")
                .OrderByDescending(row => row.UpdatedAt)
                .Take(15)
                .Select(row => new LogRow(
                    row.Id, row.BusinessId, row.UserId, row.Channel, row.TemplateType, row.TemplateKey,
                    row.Status, row.Attempts, row.Error, row.RuleId, row.SourceJobId, row.CreatedAt,
                    row.UpdatedAt, row.NextAttemptAt, row.DeliveredAt, row.DeadLetteredAt))
                .ToListAsync(ct),
            ct);

        var statistics = await BuildBusinessStatisticsAsync(businessId, ct);

        return new AdminNotificationBusinessDetailDto
        {
            BusinessId = summary.BusinessId,
            BusinessName = summary.BusinessName,
            TotalNotifications = summary.TotalNotifications,
            Sent = summary.Sent,
            Failed = summary.Failed,
            Pending = summary.Pending,
            DeliverySuccessRatePercent = summary.DeliverySuccessRatePercent,
            ChannelOverrides = summary.ChannelOverrides,
            DeliveryDisabled = summary.DeliveryDisabled,
            LastNotificationAtUtc = summary.LastNotificationAtUtc,
            EnabledChannels = enabledChannels,
            Preferences = preferences,
            Templates = templates,
            AutomationRules = rules,
            RecentNotifications = recent,
            RecentFailures = failures,
            Statistics = statistics,
        };
    }

    /// <summary>Aggregated notification posture for one business.</summary>
    private async Task<AdminNotificationBusinessSummaryDto> BuildBusinessSummaryAsync(
        Guid businessId,
        string businessName,
        CancellationToken ct)
    {
        var logs = _context.NotificationLogs.AsNoTracking().Where(row => row.BusinessId == businessId);

        var total = await logs.CountAsync(ct);
        var sent = await logs.CountAsync(row => row.Status == "sent", ct);
        var failed = await logs.CountAsync(row => row.Status == "failed", ct);
        var pending = await logs.CountAsync(row => row.Status == "pending", ct);
        var lastAt = await logs
            .OrderByDescending(row => row.CreatedAt)
            .Select(row => (DateTime?)row.CreatedAt)
            .FirstOrDefaultAsync(ct);

        var killSwitch = await _context.NotificationPreferences
            .AsNoTracking()
            .Where(row => row.BusinessId == businessId && row.UserId == null && !row.Enabled)
            .Select(row => new { row.Channel, row.UpdatedAt })
            .ToListAsync(ct);

        var overrides = killSwitch
            .GroupBy(row => row.Channel, StringComparer.Ordinal)
            .Select(group => new AdminNotificationChannelOverrideDto
            {
                Channel = group.Key,
                Enabled = false,
                UpdatedAtUtc = group.Max(entry => entry.UpdatedAt),
            })
            .ToList();

        // Delivery is considered disabled for a channel only when the business
        // (or an administrator) has suppressed every category on it.
        var suppressedAll = new List<string>();
        foreach (var channel in overrides.Select(entry => entry.Channel))
        {
            var suppressedCategories = killSwitch.Count(row => row.Channel == channel);
            if (suppressedCategories >= NotificationDefaults.AllCategories.Count)
                suppressedAll.Add(channel);
        }

        return new AdminNotificationBusinessSummaryDto
        {
            BusinessId = businessId,
            BusinessName = businessName,
            TotalNotifications = total,
            Sent = sent,
            Failed = failed,
            Pending = pending,
            DeliverySuccessRatePercent = NotificationManagementShared.Percent(sent, sent + failed),
            ChannelOverrides = overrides,
            DeliveryDisabled = suppressedAll.Count > 0,
            LastNotificationAtUtc = lastAt,
        };
    }

    private static LogRow ToLogRow(NotificationLog row) => new(

        row.Id, row.BusinessId, row.UserId, row.Channel, row.TemplateType, row.TemplateKey,
        row.Status, row.Attempts, row.Error, row.RuleId, row.SourceJobId, row.CreatedAt,
        row.UpdatedAt, row.NextAttemptAt, row.DeliveredAt, row.DeadLetteredAt);

    /// <summary>
    /// Resolves business names and recipient identities for a batch of rows, then
    /// masks the recipients. Masking happens here — the only place a recipient
    /// contact is read for an admin projection — so no caller can forget it.
    /// </summary>
    private async Task<List<AdminNotificationLogItemDto>> ProjectLogsAsync(
        IReadOnlyList<LogRow> rows,
        CancellationToken ct)
    {
        if (rows.Count == 0) return [];

        var businessIds = rows
            .Where(row => row.BusinessId.HasValue)
            .Select(row => row.BusinessId!.Value)
            .Distinct()
            .ToList();

        var businessNames = businessIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _context.Businesses
                .AsNoTracking()
                .Where(business => businessIds.Contains(business.Id))
                .Select(business => new { business.Id, business.Name })
                .ToDictionaryAsync(entry => entry.Id, entry => entry.Name, ct);

        var userIds = rows.Select(row => row.UserId).Distinct().ToList();
        var contacts = await _context.Users
            .AsNoTracking()
            .Where(user => userIds.Contains(user.Id))
            .Select(user => new { user.Id, user.Email, user.PhoneNumber })
            .ToDictionaryAsync(
                entry => entry.Id,
                entry => (entry.Email, entry.PhoneNumber),
                ct);

        var items = new List<AdminNotificationLogItemDto>(rows.Count);
        foreach (var row in rows)
        {
            contacts.TryGetValue(row.UserId, out var contact);
            items.Add(new AdminNotificationLogItemDto
            {
                Id = row.Id,
                BusinessId = row.BusinessId,
                BusinessName = row.BusinessId.HasValue && businessNames.TryGetValue(row.BusinessId.Value, out var name)
                    ? name
                    : null,
                Channel = row.Channel,
                NotificationType = row.TemplateType,
                TemplateKey = row.TemplateKey,
                Status = row.Status,
                Classification = NotificationManagementShared.Classify(row.Status, row.Error),
                RecipientMasked = NotificationManagementShared.MaskRecipient(contact.Email, contact.PhoneNumber, row.UserId),
                Attempts = row.Attempts,
                RetryEligible = NotificationManagementShared.IsRetryEligible(row.Status, row.Error),
                Cancellable = NotificationManagementShared.IsCancellable(row.Status),
                RuleId = row.RuleId,
                SourceJobId = row.SourceJobId,
                ErrorSummary = NotificationManagementShared.SummarizeError(row.Error),
                CreatedAtUtc = row.CreatedAt,
                UpdatedAtUtc = row.UpdatedAt,
                NextAttemptAtUtc = row.NextAttemptAt,
                DeliveredAtUtc = row.DeliveredAt,
                DeadLetteredAtUtc = row.DeadLetteredAt,
            });
        }

        return items;
    }

    /// <summary>A ledger row flattened for projection without tracking entities.</summary>
    private readonly record struct LogRow(
        Guid Id,
        Guid? BusinessId,
        Guid UserId,
        string Channel,
        string TemplateType,
        string? TemplateKey,
        string Status,
        int Attempts,
        string? Error,
        string? RuleId,
        string? SourceJobId,
        DateTime CreatedAt,
        DateTime UpdatedAt,
        DateTime NextAttemptAt,
        DateTime? DeliveredAt,
        DateTime? DeadLetteredAt);
}
