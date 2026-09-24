using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.Notifications;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Infrastructure.Notifications;

/// <summary>Polls the existing notification ledger and invokes external channels.</summary>
public sealed partial class NotificationDeliveryWorker : BackgroundService
{
    private const int BatchSize = 50;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(20);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<NotificationDeliveryWorker> _logger;

    public NotificationDeliveryWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<NotificationDeliveryWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Notification delivery worker started. Poll interval: {PollInterval}", PollInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Notification delivery worker failed to process an outbox batch.");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }

        _logger.LogInformation("Notification delivery worker stopped.");
    }

    /// <summary>Runs one independently scoped batch; public for deterministic worker tests.</summary>
    public async Task<int> ProcessBatchAsync(CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var store = provider.GetRequiredService<NotificationOutboxStore>();
        var context = provider.GetRequiredService<ApplicationDbContext>();
        var channels = provider.GetServices<INotificationChannel>().ToArray();
        var claimed = await store.ClaimAsync(BatchSize, ct);

        foreach (var claimedRow in claimed)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var row = await context.NotificationLogs.SingleAsync(item => item.Id == claimedRow.Id, ct);
                var channel = Array.Find(channels, candidate =>
                    string.Equals(candidate.Name, row.Channel, StringComparison.Ordinal));

                if (channel is null)
                {
                    MarkFailed(row, "no_channel_registered");
                    await context.SaveChangesAsync(ct);
                    _logger.LogInformation(
                        "Notification delivery terminal {OutboxId} on channel {Channel}: no_channel_registered",
                        row.Id, row.Channel);
                    continue;
                }

                await channel.SendAsync(BuildMessage(row), ct);
                var now = DateTime.UtcNow;
                row.Status = "sent";
                row.SentAt = now;
                row.UpdatedAt = now;
                row.Error = null;
                await context.SaveChangesAsync(ct);
                _logger.LogInformation(
                    "Notification delivery terminal {OutboxId} on channel {Channel}: sent",
                    row.Id, row.Channel);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                ct.ThrowIfCancellationRequested();
                var row = await context.NotificationLogs.SingleAsync(item => item.Id == claimedRow.Id, ct);
                row.Attempts++;
                row.UpdatedAt = DateTime.UtcNow;
                if (row.Attempts >= 3)
                {
                    MarkFailed(row, ex.Message);
                    await context.SaveChangesAsync(ct);
                    _logger.LogInformation(
                        "Notification delivery terminal {OutboxId} on channel {Channel} after {Attempts} attempts",
                        row.Id, row.Channel, row.Attempts);
                }
                else
                {
                    row.Status = "pending";
                    row.NextAttemptAt = row.UpdatedAt.Add(row.Attempts == 1
                        ? TimeSpan.FromSeconds(30)
                        : TimeSpan.FromMinutes(5));
                    await context.SaveChangesAsync(ct);
                    _logger.LogWarning(
                        "Notification delivery retry {OutboxId} on channel {Channel}; attempt {Attempts} scheduled for {NextAttemptAt}",
                        row.Id, row.Channel, row.Attempts, row.NextAttemptAt);
                }
            }
        }

        return claimed.Count;
    }

    private static void MarkFailed(NotificationLog row, string error)
    {
        row.Status = "failed";
        row.Error = error.Length <= 500 ? error : error[..500];
        row.UpdatedAt = DateTime.UtcNow;
    }

    private static ChannelMessage BuildMessage(NotificationLog row)
    {
        var data = DeserializePayload(row.PayloadJson);
        var definition = NotificationTypes.Get(row.TemplateType);
        data["renderedTitle"] = Render(definition.TitleTemplate, data);
        data["renderedBody"] = Render(definition.BodyTemplate, data);
        return new ChannelMessage(
            row.Id,
            row.TemplateType,
            row.UserId,
            row.BusinessId,
            definition.Category,
            data);
    }

    private static Dictionary<string, object?> DeserializePayload(string payload)
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(payload) ?? new();
        return values.ToDictionary(pair => pair.Key, pair => (object?)pair.Value);
    }

    private static string Render(string template, IReadOnlyDictionary<string, object?> data) =>
        TemplateToken().Replace(template, match =>
        {
            var key = match.Groups[1].Value;
            return data.TryGetValue(key, out var value) ? JsonSerializer.Serialize(value) : match.Value;
        });

    [GeneratedRegex(@"\{\{([^{}]+)\}\}")]
    private static partial Regex TemplateToken();
}
