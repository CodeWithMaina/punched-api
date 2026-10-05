using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Notifications;
using PunchedApi.Application.Settings;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

public sealed class AdminNotificationOperationsService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] KnownChannels =
    [
        NotificationChannel.InApp,
        NotificationChannel.Email,
        NotificationChannel.Sms,
        NotificationChannel.Push
    ];

    private readonly ApplicationDbContext _context;
    private readonly EmailSettings _emailSettings;
    private readonly HashSet<string> _registeredChannels;

    public AdminNotificationOperationsService(
        ApplicationDbContext context,
        IEnumerable<INotificationChannel> channels,
        IOptions<EmailSettings> emailSettings)
    {
        _context = context;
        _emailSettings = emailSettings.Value;
        _registeredChannels = channels.Select(channel => channel.Name).ToHashSet(StringComparer.Ordinal);
    }

    public static bool IsSupportedRange(string? range) => range is "1h" or "24h" or "7d" or "30d";

    public async Task<AdminNotificationOperationsOverviewDto> GetOverviewAsync(
        string range,
        CancellationToken cancellationToken = default)
    {
        var window = GetWindow(range);
        var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = (NpgsqlCommand)connection.CreateCommand();
        command.CommandText = OverviewSql;
        command.Parameters.Add("from_utc", NpgsqlDbType.TimestampTz).Value = window.FromUtc;
        command.Parameters.Add("to_utc", NpgsqlDbType.TimestampTz).Value = window.ToUtc;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("The notification overview query returned no result.");

        var notificationRecords = reader.GetInt64(0);
        var sent = reader.GetInt64(1);
        var terminalFailures = reader.GetInt64(2);
        var retryExhausted = reader.GetInt64(3);
        var permanentFailures = reader.GetInt64(4);
        var attemptsComplete = reader.GetBoolean(5);
        var totalAttempts = reader.GetInt64(6);
        var retryAttempts = reader.GetInt64(7);
        var pending = reader.GetInt64(10);
        var processing = reader.GetInt64(11);
        var oldestPendingAt = reader.IsDBNull(12) ? (DateTime?)null : reader.GetDateTime(12);
        var lastDeliveryAt = reader.IsDBNull(13) ? (DateTime?)null : reader.GetDateTime(13);
        var channelJson = reader.GetString(14);

        var channelCounts = JsonSerializer.Deserialize<List<ChannelCount>>(channelJson, JsonOptions) ?? [];
        var countByChannel = channelCounts.ToDictionary(item => item.Channel, StringComparer.Ordinal);
        var completedRecords = sent + terminalFailures;

        return new AdminNotificationOperationsOverviewDto
        {
            Range = range,
            FromUtc = window.FromUtc,
            ToUtc = window.ToUtc,
            UpdatedAtUtc = DateTime.UtcNow,
            NotificationRecords = notificationRecords,
            TotalAttempts = attemptsComplete ? totalAttempts : null,
            RetryAttempts = attemptsComplete ? retryAttempts : null,
            AttemptHistoryComplete = attemptsComplete,
            SuccessfulDeliveries = sent,
            TerminalFailures = terminalFailures,
            PendingNotifications = pending,
            RetryExhaustedNotifications = retryExhausted,
            PermanentFailures = permanentFailures,
            DeliverySuccessRatePercent = completedRecords == 0 ? null : sent * 100d / completedRecords,
            ProcessingNotifications = processing,
            OldestPendingAgeSeconds = oldestPendingAt.HasValue
                ? Math.Max(0, (window.ToUtc - DateTime.SpecifyKind(oldestPendingAt.Value, DateTimeKind.Utc)).TotalSeconds)
                : null,
            LastSuccessfulDeliveryAtUtc = lastDeliveryAt,
            WorkerActivityAvailable = false,
            Channels = BuildChannelHealth(countByChannel)
        };
    }

    public async Task<AdminNotificationTrendsDto> GetTrendsAsync(
        string range,
        CancellationToken cancellationToken = default)
    {
        var window = GetWindow(range);
        var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = (NpgsqlCommand)connection.CreateCommand();
        command.CommandText = TrendsSql;
        command.Parameters.Add("from_utc", NpgsqlDbType.TimestampTz).Value = window.FromUtc;
        command.Parameters.Add("to_utc", NpgsqlDbType.TimestampTz).Value = window.ToUtc;
        command.Parameters.Add("bucket_interval", NpgsqlDbType.Interval).Value = window.BucketSize;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("The notification trends query returned no result.");

        var complete = reader.GetBoolean(0);
        var attempts = reader.GetInt64(1);
        var retryAttempts = reader.GetInt64(2);
        var successfulAttempts = reader.GetInt64(3);
        var failedAttempts = reader.GetInt64(4);
        var pointsJson = reader.GetString(5);
        return new AdminNotificationTrendsDto
        {
            Range = range,
            FromUtc = window.FromUtc,
            ToUtc = window.ToUtc,
            UpdatedAtUtc = DateTime.UtcNow,
            AttemptHistoryComplete = complete,
            TotalAttempts = complete ? attempts : null,
            RetryAttempts = complete ? retryAttempts : null,
            SuccessfulAttempts = successfulAttempts,
            FailedAttempts = failedAttempts,
            Points = JsonSerializer.Deserialize<List<AdminNotificationTrendPointDto>>(pointsJson, JsonOptions) ?? []
        };
    }

    public async Task<PaginatedResponse<AdminNotificationFailureDto>> GetFailuresAsync(
        AdminNotificationFailureQuery query,
        CancellationToken cancellationToken = default)
    {
        var window = GetWindow(query.Range);
        var page = Math.Clamp(query.Page, 1, 100_000);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);
        var conditions = BuildFailureConditions(query);
        var where = string.Join(" AND ", conditions);
        var connection = await OpenConnectionAsync(cancellationToken);

        await using var countCommand = (NpgsqlCommand)connection.CreateCommand();
        countCommand.CommandText = $"SELECT COUNT(*) FROM notifications n WHERE {where};";
        AddFailureParameters(countCommand, query, window.FromUtc, window.ToUtc);
        var totalCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken));

        await using var pageCommand = (NpgsqlCommand)connection.CreateCommand();
        pageCommand.CommandText = $"""
            SELECT n.id, n.business_id, n.channel, n.template_type, n.status,
                   n.error, n.attempts, n.delivery_attempts_json::text, n.updated_at
            FROM notifications n
            WHERE {where}
            ORDER BY n.updated_at DESC, n.id DESC
            LIMIT @page_size OFFSET @offset;
            """;
        AddFailureParameters(pageCommand, query, window.FromUtc, window.ToUtc);
        pageCommand.Parameters.Add("page_size", NpgsqlDbType.Integer).Value = pageSize;
        pageCommand.Parameters.Add("offset", NpgsqlDbType.Integer).Value = checked((page - 1) * pageSize);

        var items = new List<AdminNotificationFailureDto>(pageSize);
        await using (var reader = await pageCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                items.Add(ReadFailure(reader));
        }

        return new PaginatedResponse<AdminNotificationFailureDto>
        {
            Items = items,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<AdminNotificationFailureDetailDto?> GetFailureDetailAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = (NpgsqlCommand)connection.CreateCommand();
        command.CommandText = """
            SELECT n.id, n.business_id, n.channel, n.template_type, n.status,
                   n.error, n.attempts, n.delivery_attempts_json::text, n.updated_at
            FROM notifications n
            WHERE n.id = @id AND n.status = 'failed';
            """;
        command.Parameters.Add("id", NpgsqlDbType.Uuid).Value = id;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        var failure = ReadFailure(reader);
        var history = NotificationDeliveryAttemptHistory.Deserialize(reader.IsDBNull(7) ? null : reader.GetString(7));
        return new AdminNotificationFailureDetailDto
        {
            Id = failure.Id,
            BusinessId = failure.BusinessId,
            Channel = failure.Channel,
            NotificationType = failure.NotificationType,
            Classification = failure.Classification,
            RetryFailures = failure.RetryFailures,
            DeliveryAttempts = history.Complete ? history.Attempts.Count : null,
            AttemptHistoryComplete = history.Complete,
            LastAttemptAtUtc = failure.LastAttemptAtUtc,
            Status = failure.Status,
            RetryEligible = failure.RetryEligible,
            UpdatedAtUtc = failure.UpdatedAtUtc,
            SanitizedError = SanitizeError(failure.Classification, reader.IsDBNull(5) ? null : reader.GetString(5)),
            AttemptsHistory = history.Attempts.Select(attempt => new AdminNotificationAttemptDto
            {
                StartedAtUtc = attempt.StartedAtUtc,
                CompletedAtUtc = attempt.CompletedAtUtc,
                Outcome = attempt.Outcome
            }).ToList()
        };
    }

    public async Task<RetryDecision> RetryAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _context.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken);

        var row = await _context.NotificationLogs.AsNoTracking()
            .Where(notification => notification.Id == id)
            .Select(notification => new
            {
                notification.UserId,
                notification.BusinessId,
                notification.Channel,
                notification.Status,
                notification.Error
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (row is null) return RetryDecision.NotFound();
        if (row.Status != "failed" || row.Error is null || !row.Error.StartsWith("retry_exhausted:", StringComparison.Ordinal))
            return RetryDecision.Rejected("RETRY_NOT_ELIGIBLE", "Only retry-exhausted failures can be requeued.");
        if (!_registeredChannels.Contains(row.Channel))
            return RetryDecision.Rejected("CHANNEL_UNAVAILABLE", "The delivery channel is not registered in this deployment.");
        if (row.Channel == NotificationChannel.Email &&
            (!_emailSettings.Enabled || string.IsNullOrWhiteSpace(_emailSettings.Host) || string.IsNullOrWhiteSpace(_emailSettings.FromAddress)))
            return RetryDecision.Rejected("CHANNEL_UNAVAILABLE", "Email delivery is not enabled and configured.");

        var recipient = await _context.Users.Include(user => user.Auth).AsNoTracking()
            .SingleOrDefaultAsync(user => user.Id == row.UserId, cancellationToken);
        if (recipient is null || recipient.IsDeleted)
            return RetryDecision.Rejected("RECIPIENT_UNAVAILABLE", "The notification recipient is no longer available.");
        if (row.Channel == NotificationChannel.Email &&
            (string.IsNullOrWhiteSpace(recipient.Email) || recipient.Auth?.IsVerified != true))
            return RetryDecision.Rejected("RECIPIENT_UNAVAILABLE", "The notification recipient no longer has a verified email address.");

        if (row.BusinessId is Guid businessId)
        {
            var associated = await _context.Businesses.AnyAsync(
                    business => business.Id == businessId && !business.IsDeleted && business.OwnerId == recipient.Id,
                    cancellationToken)
                || recipient.StaffBusinessId == businessId
                || await _context.CustomerBusinessEnrollments.AnyAsync(
                    enrollment => enrollment.BusinessId == businessId && enrollment.CustomerId == recipient.Id,
                    cancellationToken);
            if (!associated)
                return RetryDecision.Rejected("TENANT_ASSOCIATION_CHANGED", "The recipient is no longer associated with the notification business.");
        }

        var now = DateTime.UtcNow;
        var changed = await _context.NotificationLogs
            .Where(notification => notification.Id == id && notification.Status == "failed" &&
                                   notification.Error != null && notification.Error.StartsWith("retry_exhausted:"))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(notification => notification.Status, "pending")
                .SetProperty(notification => notification.Attempts, 0)
                .SetProperty(notification => notification.Error, (string?)null)
                .SetProperty(notification => notification.NextAttemptAt, now)
                .SetProperty(notification => notification.UpdatedAt, now), cancellationToken);

        if (changed != 1)
            return RetryDecision.Rejected("RETRY_ALREADY_HANDLED", "The notification changed before retry was accepted.");

        await transaction.CommitAsync(cancellationToken);
        return RetryDecision.Accepted(id);
    }

    private List<AdminNotificationChannelHealthDto> BuildChannelHealth(IReadOnlyDictionary<string, ChannelCount> counts)
    {
        var channelNames = KnownChannels.Concat(counts.Keys).Distinct(StringComparer.Ordinal);
        return channelNames.Select(channel =>
        {
            var implemented = channel == NotificationChannel.InApp || _registeredChannels.Contains(channel);
            var configured = channel switch
            {
                NotificationChannel.InApp => true,
                NotificationChannel.Email => !string.IsNullOrWhiteSpace(_emailSettings.Host) &&
                                             !string.IsNullOrWhiteSpace(_emailSettings.FromAddress),
                _ => implemented
            };
            var enabled = channel == NotificationChannel.InApp ||
                          (channel == NotificationChannel.Email && _emailSettings.Enabled);
            var countsForChannel = counts.GetValueOrDefault(channel) ?? new ChannelCount(channel, 0, 0, 0, 0, 0);
            return new AdminNotificationChannelHealthDto
            {
                Channel = channel,
                Implemented = implemented,
                Configured = configured,
                Enabled = enabled,
                ActiveInRange = countsForChannel.Records > 0,
                RecordsInRange = countsForChannel.Records,
                SentInRange = countsForChannel.Sent,
                FailedInRange = countsForChannel.Failed,
                PendingNow = countsForChannel.Pending,
                ProcessingNow = countsForChannel.Processing
            };
        }).ToList();
    }

    private static List<string> BuildFailureConditions(AdminNotificationFailureQuery query)
    {
        var conditions = new List<string>
        {
            "n.status = 'failed'",
            "n.updated_at >= @from_utc",
            "n.updated_at < @to_utc"
        };
        if (!string.IsNullOrWhiteSpace(query.Channel)) conditions.Add("n.channel = @channel");
        if (!string.IsNullOrWhiteSpace(query.NotificationType)) conditions.Add("n.template_type = @notification_type");
        if (query.BusinessId.HasValue) conditions.Add("n.business_id = @business_id");
        if (!string.IsNullOrWhiteSpace(query.Classification))
        {
            conditions.Add(query.Classification switch
            {
                "retry_exhausted" => "n.error LIKE 'retry_exhausted:%'",
                "permanent" => "n.error LIKE 'permanent:%'",
                "unknown" => "(n.error IS NULL OR (n.error NOT LIKE 'retry_exhausted:%' AND n.error NOT LIKE 'permanent:%'))",
                _ => throw new ArgumentException("Unsupported failure classification.", nameof(query))
            });
        }
        if (query.RetryEligible == true) conditions.Add("n.error LIKE 'retry_exhausted:%'");
        if (query.RetryEligible == false) conditions.Add("(n.error IS NULL OR n.error NOT LIKE 'retry_exhausted:%')");
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            conditions.Add(Guid.TryParse(query.Search, out _)
                ? "(n.id = @search_id OR n.business_id = @search_id)"
                : "n.template_type = @search_type");
        }
        return conditions;
    }

    private static void AddFailureParameters(
        NpgsqlCommand command,
        AdminNotificationFailureQuery query,
        DateTime fromUtc,
        DateTime toUtc)
    {
        command.Parameters.Add("from_utc", NpgsqlDbType.TimestampTz).Value = fromUtc;
        command.Parameters.Add("to_utc", NpgsqlDbType.TimestampTz).Value = toUtc;
        if (!string.IsNullOrWhiteSpace(query.Channel))
            command.Parameters.Add("channel", NpgsqlDbType.Text).Value = query.Channel!;
        if (!string.IsNullOrWhiteSpace(query.NotificationType))
            command.Parameters.Add("notification_type", NpgsqlDbType.Text).Value = query.NotificationType!;
        if (query.BusinessId.HasValue)
            command.Parameters.Add("business_id", NpgsqlDbType.Uuid).Value = query.BusinessId.Value;
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            if (Guid.TryParse(query.Search, out var searchId))
                command.Parameters.Add("search_id", NpgsqlDbType.Uuid).Value = searchId;
            else
                command.Parameters.Add("search_type", NpgsqlDbType.Text).Value = query.Search.Trim();
        }
    }

    private static AdminNotificationFailureDto ReadFailure(NpgsqlDataReader reader)
    {
        var historyJson = reader.IsDBNull(7) ? null : reader.GetString(7);
        var history = NotificationDeliveryAttemptHistory.Deserialize(historyJson);
        var error = reader.IsDBNull(5) ? null : reader.GetString(5);
        return new AdminNotificationFailureDto
        {
            Id = reader.GetGuid(0),
            BusinessId = reader.IsDBNull(1) ? null : reader.GetGuid(1),
            Channel = reader.GetString(2),
            NotificationType = reader.GetString(3),
            Classification = GetClassification(error),
            RetryFailures = reader.GetInt32(6),
            DeliveryAttempts = history.Complete ? history.Attempts.Count : null,
            AttemptHistoryComplete = history.Complete,
            LastAttemptAtUtc = history.Attempts.LastOrDefault()?.StartedAtUtc,
            Status = reader.GetString(4),
            RetryEligible = IsRetryEligible(reader.GetString(4), error),
            UpdatedAtUtc = DateTime.SpecifyKind(reader.GetDateTime(8), DateTimeKind.Utc)
        };
    }

    private static string GetClassification(string? error)
    {
        if (error?.StartsWith("retry_exhausted:", StringComparison.Ordinal) == true) return "retry_exhausted";
        if (error?.StartsWith("permanent:", StringComparison.Ordinal) == true) return "permanent";
        return "unknown";
    }

    private static bool IsRetryEligible(string status, string? error) =>
        status == "failed" && error?.StartsWith("retry_exhausted:", StringComparison.Ordinal) == true;

    private static string SanitizeError(string classification, string? error) => classification switch
    {
        "retry_exhausted" => "Delivery remained unsuccessful after the configured retry attempts.",
        "permanent" when error?.Contains("no_channel_registered", StringComparison.Ordinal) == true =>
            "No delivery channel is registered in this deployment.",
        "permanent" when error?.Contains("not eligible for email", StringComparison.OrdinalIgnoreCase) == true =>
            "The recipient is not eligible for email delivery.",
        "permanent" when error?.Contains("not enabled", StringComparison.OrdinalIgnoreCase) == true =>
            "Email delivery is disabled or unavailable.",
        "permanent" => "The channel classified this delivery as a permanent failure.",
        _ => "This legacy failure has no recognized classification; inspect restricted application logs."
    };

    private static TimeWindow GetWindow(string range)
    {
        var now = DateTime.UtcNow;
        var (duration, bucket) = range switch
        {
            "1h" => (TimeSpan.FromHours(1), TimeSpan.FromMinutes(5)),
            "24h" => (TimeSpan.FromHours(24), TimeSpan.FromHours(1)),
            "7d" => (TimeSpan.FromDays(7), TimeSpan.FromDays(1)),
            "30d" => (TimeSpan.FromDays(30), TimeSpan.FromDays(1)),
            _ => throw new ArgumentException("Range must be one of 1h, 24h, 7d, or 30d.", nameof(range))
        };
        return new TimeWindow(now - duration, now, bucket);
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)_context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private const string OverviewSql = """
        WITH attempt_events AS (
            SELECT n.channel,
                   attempt.ordinality,
                   attempt.value->>'outcome' AS outcome
            FROM notifications n
            CROSS JOIN LATERAL jsonb_array_elements(
                CASE WHEN jsonb_typeof(n.delivery_attempts_json->'attempts') = 'array'
                     THEN n.delivery_attempts_json->'attempts' ELSE '[]'::jsonb END
            ) WITH ORDINALITY AS attempt(value, ordinality)
                        WHERE n.updated_at >= @from_utc AND n.updated_at < @to_utc
                            AND COALESCE((attempt.value->>'completedAtUtc')::timestamptz,
                                                     (attempt.value->>'startedAtUtc')::timestamptz) >= @from_utc
                            AND COALESCE((attempt.value->>'completedAtUtc')::timestamptz,
                                                     (attempt.value->>'startedAtUtc')::timestamptz) < @to_utc
        ),
        attempt_totals AS (
            SELECT COUNT(*)::bigint AS attempts,
                   COUNT(*) FILTER (WHERE ordinality > 1)::bigint AS retry_attempts,
                   COUNT(*) FILTER (WHERE outcome = 'sent')::bigint AS successful_attempts,
                   COUNT(*) FILTER (WHERE outcome IN ('retryable_failure', 'permanent_failure'))::bigint AS failed_attempts
            FROM attempt_events
        ),
        channel_counts AS (
            SELECT channel,
                   COUNT(*) FILTER (WHERE created_at >= @from_utc AND created_at < @to_utc)::bigint AS records,
                   COUNT(*) FILTER (WHERE status = 'sent' AND sent_at >= @from_utc AND sent_at < @to_utc)::bigint AS sent,
                   COUNT(*) FILTER (WHERE status = 'failed' AND updated_at >= @from_utc AND updated_at < @to_utc)::bigint AS failed,
                   COUNT(*) FILTER (WHERE status = 'failed' AND error LIKE 'retry_exhausted:%' AND updated_at >= @from_utc AND updated_at < @to_utc)::bigint AS retry_exhausted,
                   COUNT(*) FILTER (WHERE status = 'failed' AND error LIKE 'permanent:%' AND updated_at >= @from_utc AND updated_at < @to_utc)::bigint AS permanent,
                   COUNT(*) FILTER (WHERE status = 'pending')::bigint AS pending,
                   COUNT(*) FILTER (WHERE status = 'processing')::bigint AS processing
            FROM notifications
            WHERE (created_at >= @from_utc AND created_at < @to_utc) OR
                  (updated_at >= @from_utc AND updated_at < @to_utc) OR
                  (sent_at >= @from_utc AND sent_at < @to_utc) OR status IN ('pending', 'processing')
            GROUP BY channel
        ),
        notification_totals AS (
            SELECT COALESCE(SUM(records), 0)::bigint AS records,
                   COALESCE(SUM(sent), 0)::bigint AS sent,
                   COALESCE(SUM(failed), 0)::bigint AS failed,
                   COALESCE(SUM(retry_exhausted), 0)::bigint AS retry_exhausted,
                   COALESCE(SUM(permanent), 0)::bigint AS permanent,
                   COALESCE(SUM(pending), 0)::bigint AS pending,
                   COALESCE(SUM(processing), 0)::bigint AS processing
            FROM channel_counts
        )
        SELECT
            (SELECT records FROM notification_totals),
            (SELECT sent FROM notification_totals),
            (SELECT failed FROM notification_totals),
            (SELECT retry_exhausted FROM notification_totals),
            (SELECT permanent FROM notification_totals),
            NOT EXISTS (
                SELECT 1 FROM notifications n
                WHERE n.updated_at >= @from_utc AND n.updated_at < @to_utc
                  AND (
                    (n.delivery_attempts_json IS NULL AND
                     (n.status IN ('sent', 'processing') OR n.attempts > 0 OR
                      (n.status = 'failed' AND n.error IS DISTINCT FROM 'no_channel_registered' AND n.error IS DISTINCT FROM 'permanent:no_channel_registered')))
                    OR (n.delivery_attempts_json IS NOT NULL AND n.delivery_attempts_json->>'complete' IS DISTINCT FROM 'true')
                  )
            ),
            (SELECT attempts FROM attempt_totals),
            (SELECT retry_attempts FROM attempt_totals),
            (SELECT successful_attempts FROM attempt_totals),
            (SELECT failed_attempts FROM attempt_totals),
            (SELECT pending FROM notification_totals),
            (SELECT processing FROM notification_totals),
            (SELECT MIN(created_at) FROM notifications WHERE status = 'pending'),
            (SELECT MAX(sent_at) FROM notifications WHERE status = 'sent' AND sent_at >= @from_utc AND sent_at < @to_utc),
            COALESCE((SELECT jsonb_agg(jsonb_build_object(
                'channel', channel, 'records', records, 'sent', sent, 'failed', failed,
                'retryExhausted', retry_exhausted, 'permanent', permanent,
                'pending', pending, 'processing', processing
            ) ORDER BY channel) FROM channel_counts), '[]'::jsonb)::text;
        """;

    private const string TrendsSql = """
        WITH attempt_events AS (
            SELECT n.channel,
                   COALESCE((attempt.value->>'completedAtUtc')::timestamptz,
                            (attempt.value->>'startedAtUtc')::timestamptz) AS event_at,
                   attempt.ordinality,
                   attempt.value->>'outcome' AS outcome
            FROM notifications n
            CROSS JOIN LATERAL jsonb_array_elements(
                CASE WHEN jsonb_typeof(n.delivery_attempts_json->'attempts') = 'array'
                     THEN n.delivery_attempts_json->'attempts' ELSE '[]'::jsonb END
            ) WITH ORDINALITY AS attempt(value, ordinality)
                        WHERE n.updated_at >= @from_utc AND n.updated_at < @to_utc
              AND COALESCE((attempt.value->>'completedAtUtc')::timestamptz,
                           (attempt.value->>'startedAtUtc')::timestamptz) >= @from_utc
                            AND COALESCE((attempt.value->>'completedAtUtc')::timestamptz,
                                                     (attempt.value->>'startedAtUtc')::timestamptz) < @to_utc
        ),
        points AS (
            SELECT date_bin(@bucket_interval, event_at, @from_utc) AS bucket_utc, channel,
                   COUNT(*) FILTER (WHERE outcome = 'sent')::bigint AS successful_attempts,
                   COUNT(*) FILTER (WHERE outcome IN ('retryable_failure', 'permanent_failure'))::bigint AS failed_attempts
            FROM attempt_events
            GROUP BY bucket_utc, channel
        )
        SELECT
            NOT EXISTS (
                SELECT 1 FROM notifications n
                WHERE n.updated_at >= @from_utc AND n.updated_at < @to_utc
                  AND ((n.delivery_attempts_json IS NULL AND
                        (n.status IN ('sent', 'processing') OR n.attempts > 0 OR
                         (n.status = 'failed' AND n.error IS DISTINCT FROM 'no_channel_registered' AND n.error IS DISTINCT FROM 'permanent:no_channel_registered')))
                       OR (n.delivery_attempts_json IS NOT NULL AND n.delivery_attempts_json->>'complete' IS DISTINCT FROM 'true'))
            ),
            (SELECT COUNT(*)::bigint FROM attempt_events),
            (SELECT COUNT(*) FILTER (WHERE ordinality > 1)::bigint FROM attempt_events),
            (SELECT COUNT(*) FILTER (WHERE outcome = 'sent')::bigint FROM attempt_events),
            (SELECT COUNT(*) FILTER (WHERE outcome IN ('retryable_failure', 'permanent_failure'))::bigint FROM attempt_events),
            COALESCE((SELECT jsonb_agg(jsonb_build_object(
                'bucketUtc', bucket_utc, 'channel', channel,
                'successfulAttempts', successful_attempts, 'failedAttempts', failed_attempts
            ) ORDER BY bucket_utc, channel) FROM points), '[]'::jsonb)::text;
        """;

    private readonly record struct TimeWindow(DateTime FromUtc, DateTime ToUtc, TimeSpan BucketSize);
    private sealed record ChannelCount(string Channel, long Records, long Sent, long Failed, long Pending, long Processing);

    public sealed record RetryDecision(bool Found, bool WasAccepted, string Code, string Message, Guid? Id)
    {
        public static RetryDecision NotFound() => new(false, false, "NOT_FOUND", "Notification not found.", null);
        public static RetryDecision Rejected(string code, string message) => new(true, false, code, message, null);
        public static RetryDecision Accepted(Guid id) => new(true, true, "ACCEPTED", "Notification requeued for worker delivery.", id);
    }
}