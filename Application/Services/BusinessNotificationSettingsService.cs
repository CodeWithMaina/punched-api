using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MimeKit;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Notifications;
using PunchedApi.Application.Settings;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;
using PunchedApi.Infrastructure.Notifications;

namespace PunchedApi.Application.Services;

/// <summary>
/// The tenant-scoped notification workspace: overview statistics, per-channel
/// configuration, the (category × channel) preference matrix, the template
/// catalogue, previews and test sends.
///
/// Every method is scoped to a single business id the caller has already been
/// authorised for — the controller resolves tenant ownership before calling in,
/// so no method here accepts a business id it has not been given explicitly.
/// </summary>
public sealed class BusinessNotificationSettingsService
{
    /// <summary>Ledger template-key prefix marking a test send.</summary>
    public const string TestTemplateKeyPrefix = "test|";

    private const int AggregateScanLimit = 20_000;

    private readonly ApplicationDbContext _context;
    private readonly INotificationChannelAvailability _availability;
    private readonly ITemplateOverrideResolver _templates;
    private readonly ITemplateRenderer _renderer;
    private readonly EmailLayoutRenderer _layout;
    private readonly ISubscriptionAuditService _audit;
    private readonly INotificationSmtpDelivery _smtpDelivery;
    private readonly AfricaTalkingClient _smsClient;
    private readonly EmailSettings _email;
    private readonly ILogger<BusinessNotificationSettingsService> _logger;

    public BusinessNotificationSettingsService(
        ApplicationDbContext context,
        INotificationChannelAvailability availability,
        ITemplateOverrideResolver templates,
        ITemplateRenderer renderer,
        EmailLayoutRenderer layout,
        ISubscriptionAuditService audit,
        INotificationSmtpDelivery smtpDelivery,
        AfricaTalkingClient smsClient,
        IOptions<EmailSettings> email,
        ILogger<BusinessNotificationSettingsService> logger)
    {
        _context = context;
        _availability = availability;
        _templates = templates;
        _renderer = renderer;
        _layout = layout;
        _audit = audit;
        _smtpDelivery = smtpDelivery;
        _smsClient = smsClient;
        _email = email.Value;
        _logger = logger;
    }

    /// <summary>
    /// Overview statistics for one business. Test sends are excluded so the
    /// business never sees a test inflate its production delivery numbers.
    /// </summary>
    public async Task<BusinessNotificationOverviewDto> GetOverviewAsync(
        Guid businessId,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken ct = default)
    {
        if (toUtc <= fromUtc) toUtc = fromUtc.AddDays(1);

        var rows = await _context.NotificationLogs
            .AsNoTracking()
            .Where(row => row.BusinessId == businessId
                && row.CreatedAt >= fromUtc
                && row.CreatedAt <= toUtc
                && (row.TemplateKey == null || !row.TemplateKey.StartsWith(TestTemplateKeyPrefix)))
            .Select(row => new OverviewRow(
                row.Id, row.UserId, row.Channel, row.TemplateType, row.Status,
                row.CreatedAt, row.DeliveredAt))
            .Take(AggregateScanLimit)
            .ToListAsync(ct);

        var sent = rows.Count(row => row.Status == "sent");
        var failed = rows.Count(row => row.Status == "failed");
        var pending = rows.Count(row => row.Status == "pending");
        var processing = rows.Count(row => row.Status == "processing");

        var byChannel = rows
            .GroupBy(row => row.Channel, StringComparer.Ordinal)
            .Select(group => new AdminNotificationAnalyticsCountDto
            {
                Key = group.Key,
                Label = NotificationManagementShared.Humanize(group.Key),
                Total = group.Count(),
                Sent = group.Count(row => row.Status == "sent"),
                Failed = group.Count(row => row.Status == "failed"),
                Pending = group.Count(row => row.Status == "pending"),
                SuccessRatePercent = NotificationManagementShared.Percent(
                    group.Count(row => row.Status == "sent"),
                    group.Count(row => row.Status == "sent") + group.Count(row => row.Status == "failed")),
            })
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

        var recent = rows.OrderByDescending(row => row.CreatedAt).Take(10).ToList();
        var contacts = await LoadContactsAsync(recent.Select(row => row.UserId).Distinct().ToList(), ct);

        var activity = recent.Select(row =>
        {
            contacts.TryGetValue(row.UserId, out var contact);
            return new BusinessNotificationActivityDto
            {
                Id = row.Id,
                Channel = row.Channel,
                NotificationType = row.TemplateType,
                Status = row.Status,
                RecipientMasked = NotificationManagementShared.MaskRecipient(
                    contact.Email, contact.PhoneNumber, row.UserId),
                IsTest = false,
                CreatedAtUtc = row.CreatedAt,
                DeliveredAtUtc = row.DeliveredAt,
            };
        }).ToList();

        var availableChannels = await AvailableChannelsAsync(businessId, ct);
        var suppressed = await SuppressedChannelsAsync(businessId, ct);

        var (status, detail) = DescribeSystemStatus(rows.Count, sent, failed, pending, availableChannels.Count);

        return new BusinessNotificationOverviewDto
        {
            BusinessId = businessId,
            FromUtc = fromUtc,
            ToUtc = toUtc,
            UpdatedAtUtc = DateTime.UtcNow,
            SystemStatus = status,
            StatusDetail = detail,
            EnabledChannels = availableChannels.Where(channel => !suppressed.Contains(channel)).ToList(),
            AvailableChannels = availableChannels,
            TotalNotifications = rows.Count,
            Delivered = sent,
            Failed = failed,
            Pending = pending,
            Processing = processing,
            DeliverySuccessRatePercent = NotificationManagementShared.Percent(sent, sent + failed),
            ByChannel = byChannel,
            DailyTrend = dailyTrend,
            RecentActivity = activity,
        };
    }

    private static (string Status, string Detail) DescribeSystemStatus(
        int total,
        int sent,
        int failed,
        int pending,
        int availableChannels)
    {
        if (availableChannels == 0)
            return ("attention", "No notification channel is available in this deployment yet.");

        if (total == 0)
            return ("idle", "No notifications have been sent in this period yet.");

        var rate = NotificationManagementShared.Percent(sent, sent + failed);
        if (rate is null && pending > 0)
            return ("idle", $"{pending} notification(s) are queued for delivery.");

        if (rate is >= 95)
            return ("healthy", $"{rate}% of notifications were delivered successfully.");

        if (rate is >= 80)
            return ("degraded", $"{failed} notification(s) failed — review the failures below.");

        return ("attention", $"{failed} of {sent + failed} notifications failed. Check your channel configuration.");
    }

    // ══════════════════════════════════════════════════════════════════
    //  Channel configuration
    // ══════════════════════════════════════════════════════════════════

    /// <summary>Channels this deployment can deliver on, resolved for the business's owner.</summary>
    private async Task<List<string>> AvailableChannelsAsync(Guid businessId, CancellationToken ct)
    {
        var ownerId = await _context.BusinessMemberships
            .AsNoTracking()
            .Where(membership => membership.BusinessId == businessId
                && membership.Role == BusinessMembershipRole.Owner
                && membership.Status == BusinessMembershipStatus.Active)
            .OrderBy(membership => membership.CreatedAt)
            .Select(membership => (Guid?)membership.UserId)
            .FirstOrDefaultAsync(ct);

        if (ownerId is null)
        {
            // No resolvable owner: report the channels the deployment registers
            // (in-app is always deliverable — the inbox insert *is* the delivery).
            return NotificationDefaults.AllChannels.ToList();
        }

        var available = await _availability.FilterAvailableAsync(ownerId.Value, NotificationDefaults.AllChannels, ct);
        if (!available.Contains(NotificationChannel.InApp))
        {
            available = new[] { NotificationChannel.InApp }.Concat(available).Distinct(StringComparer.Ordinal).ToList();
        }

        return available.ToList();
    }

    /// <summary>Channels the business has suppressed across every category (its kill-switch).</summary>
    private async Task<HashSet<string>> SuppressedChannelsAsync(Guid businessId, CancellationToken ct)
    {
        var rows = await _context.NotificationPreferences
            .AsNoTracking()
            .Where(row => row.BusinessId == businessId && row.UserId == null && !row.Enabled)
            .Select(row => row.Channel)
            .ToListAsync(ct);

        var suppressed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var channel in rows.Distinct(StringComparer.Ordinal))
        {
            if (rows.Count(row => row == channel) >= NotificationDefaults.AllCategories.Count)
                suppressed.Add(channel);
        }

        return suppressed;
    }

    private async Task<Dictionary<Guid, (string? Email, string? PhoneNumber)>> LoadContactsAsync(
        IReadOnlyList<Guid> userIds,
        CancellationToken ct)
    {
        if (userIds.Count == 0) return new Dictionary<Guid, (string?, string?)>();

        return await _context.Users
            .AsNoTracking()
            .Where(user => userIds.Contains(user.Id))
            .Select(user => new { user.Id, user.Email, user.PhoneNumber })
            .ToDictionaryAsync(entry => entry.Id, entry => (entry.Email, entry.PhoneNumber), ct);
    }

    /// <summary>The business's own channel configuration, with platform restrictions made explicit.</summary>
    public async Task<List<BusinessNotificationChannelDto>> GetChannelsAsync(
        Guid businessId,
        CancellationToken ct = default)
    {
        var profile = await _context.BusinessMessagingProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(entity => entity.BusinessId == businessId, ct);

        var business = await _context.Businesses
            .AsNoTracking()
            .Where(entity => entity.Id == businessId)
            .Select(entity => new { entity.Name, entity.Email, entity.PhoneNumber })
            .FirstOrDefaultAsync(ct);

        var available = await AvailableChannelsAsync(businessId, ct);
        var suppressed = await SuppressedChannelsAsync(businessId, ct);

        bool IsEnabled(string channel) =>
            available.Contains(channel) && !suppressed.Contains(channel);

        return
        [
            new BusinessNotificationChannelDto
            {
                Channel = NotificationChannel.Email,
                Available = available.Contains(NotificationChannel.Email),
                Enabled = IsEnabled(NotificationChannel.Email),
                Settings = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["senderDisplayName"] = profile?.DisplayName ?? business?.Name,
                    ["replyTo"] = profile?.ReplyTo,
                    ["contactEmail"] = profile?.ReplyTo ?? business?.Email,
                    ["footerNote"] = profile?.FooterNote,
                    ["primaryColor"] = profile?.PrimaryColor,
                },
                PlatformNotes =
                [
                    "Sending is performed by Punched's verified mail service; you control the display name, reply-to address and branding.",
                    "The platform sender address cannot be changed from here.",
                ],
                VerificationStatus = string.IsNullOrWhiteSpace(profile?.ReplyTo) ? "unverified" : "configured",
                VerificationDetail = string.IsNullOrWhiteSpace(profile?.ReplyTo)
                    ? "Add a reply-to address so customers can respond to your emails."
                    : "Replies will be routed to this address.",
            },
            new BusinessNotificationChannelDto
            {
                Channel = NotificationChannel.Sms,
                Available = available.Contains(NotificationChannel.Sms),
                Enabled = IsEnabled(NotificationChannel.Sms),
                Settings = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["senderIdentity"] = business?.Name,
                    ["contactPhone"] = business?.PhoneNumber,
                },
                PlatformNotes =
                [
                    "SMS is sent through Punched's platform sender. Provider credentials are platform-owned and never exposed.",
                    "Each SMS is billed per segment; keep messages concise.",
                ],
                VerificationStatus = available.Contains(NotificationChannel.Sms) ? "configured" : "unavailable",
                VerificationDetail = available.Contains(NotificationChannel.Sms)
                    ? "SMS delivery is available on this deployment."
                    : "This deployment has not enabled SMS delivery yet.",
            },
            new BusinessNotificationChannelDto
            {
                Channel = NotificationChannel.Push,
                Available = available.Contains(NotificationChannel.Push),
                Enabled = IsEnabled(NotificationChannel.Push),
                Settings = new Dictionary<string, string?>(StringComparer.Ordinal),
                PlatformNotes =
                [
                    "Customers enable push notifications from their browser or the installed app.",
                    "VAPID keys are platform-owned and cannot be changed from the business console.",
                ],
                VerificationStatus = available.Contains(NotificationChannel.Push) ? "configured" : "unavailable",
                VerificationDetail = available.Contains(NotificationChannel.Push)
                    ? "Web push is available. Customers must opt in on their device."
                    : "This deployment has not configured web push yet.",
            },
            new BusinessNotificationChannelDto
            {
                Channel = NotificationChannel.InApp,
                Available = true,
                Enabled = !suppressed.Contains(NotificationChannel.InApp),
                Settings = new Dictionary<string, string?>(StringComparer.Ordinal),
                PlatformNotes =
                [
                    "In-app notifications are delivered to the application inbox and are always available.",
                ],
                VerificationStatus = "not_required",
                VerificationDetail = "Delivered to the in-app inbox.",
            },
        ];
    }

    /// <summary>
    /// Updates the business's messaging profile (branding and sender identity) and
    /// its channel kill-switches. Only business-owned fields are written — the
    /// platform's provider configuration is never touched.
    /// </summary>
    public async Task<List<BusinessNotificationChannelDto>> UpdateChannelsAsync(
        Guid businessId,
        UpdateBusinessNotificationChannelsRequest request,
        Guid? actorUserId,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var business = await _context.Businesses.FirstOrDefaultAsync(entity => entity.Id == businessId, ct)
            ?? throw new KeyNotFoundException($"Business {businessId} was not found.");

        var profile = await _context.BusinessMessagingProfiles
            .FirstOrDefaultAsync(entity => entity.BusinessId == businessId, ct);

        if (profile is null)
        {
            profile = new BusinessMessagingProfile
            {
                Id = Guid.NewGuid(),
                BusinessId = businessId,
                DisplayName = business.Name,
                CreatedAt = now,
            };
            _context.BusinessMessagingProfiles.Add(profile);
        }

        if (request.DisplayName is not null)
            profile.DisplayName = Bound(request.DisplayName, 200) ?? business.Name;
        if (request.ReplyTo is not null)
            profile.ReplyTo = Bound(request.ReplyTo, 200);
        if (request.FooterNote is not null)
            profile.FooterNote = Bound(request.FooterNote, 1000);
        if (request.PrimaryColor is not null)
            profile.PrimaryColor = NormalizeColor(request.PrimaryColor);
        if (request.LogoMediaId is not null)
            profile.LogoMediaId = request.LogoMediaId == Guid.Empty ? null : request.LogoMediaId;
        if (request.Timezone is not null)
            profile.Timezone = Bound(request.Timezone, 64);

        if (request.ContactEmail is not null) business.Email = Bound(request.ContactEmail, 256);
        if (request.ContactPhone is not null) business.PhoneNumber = Bound(request.ContactPhone, 32);

        if (request.EnabledChannels is { Count: > 0 } requested)
        {
            var desired = requested
                .Where(channel => NotificationDefaults.AllChannels.Contains(channel))
                .Distinct(StringComparer.Ordinal)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var channel in NotificationDefaults.AllChannels)
            {
                var rows = await _context.NotificationPreferences
                    .Where(row => row.BusinessId == businessId && row.UserId == null && row.Channel == channel)
                    .ToListAsync(ct);

                if (desired.Contains(channel))
                {
                    // Enabling clears the kill-switch so the channel returns to the
                    // system default (a business can only ever close a channel).
                    _context.NotificationPreferences.RemoveRange(rows);
                }
                else if (rows.Count < NotificationDefaults.AllCategories.Count)
                {
                    foreach (var category in NotificationDefaults.AllCategories)
                    {
                        var existing = rows.FirstOrDefault(row => row.Category == category);
                        if (existing is null)
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
                            existing.Enabled = false;
                            existing.UpdatedAt = now;
                        }
                    }
                }
            }
        }

        await _context.SaveChangesAsync(ct);

        await _audit.RecordAsync(
            "NOTIFICATION_CHANNELS_UPDATED",
            actorUserId,
            businessId,
            targetPlanId: null,
            payloadJson: System.Text.Json.JsonSerializer.Serialize(new
            {
                displayName = profile.DisplayName,
                replyTo = profile.ReplyTo,
                primaryColor = profile.PrimaryColor,
                enabledChannels = request.EnabledChannels,
            }),
            reason: "Business notification channel configuration updated.");

        return await GetChannelsAsync(businessId, ct);
    }

    private static string? Bound(string? value, int max)
    {
        if (value is null) return null;
        var trimmed = value.Trim();
        if (trimmed.Length == 0) return null;
        return trimmed.Length > max ? trimmed[..max] : trimmed;
    }

    private static string? NormalizeColor(string? color)
    {
        if (string.IsNullOrWhiteSpace(color)) return null;
        var trimmed = color.Trim();
        return System.Text.RegularExpressions.Regex.IsMatch(
            trimmed, "^#([A-Fa-f0-9]{6}|[A-Fa-f0-9]{3})$") ? trimmed : null;
    }

    // ══════════════════════════════════════════════════════════════════
    //  Preferences
    // ══════════════════════════════════════════════════════════════════

    /// <summary>The business's resolved (category × channel) matrix.</summary>
    public async Task<List<BusinessNotificationPreferenceDto>> GetPreferencesAsync(
        Guid businessId,
        CancellationToken ct = default)
    {
        var rows = await _context.NotificationPreferences
            .AsNoTracking()
            .Where(row => row.BusinessId == businessId && row.UserId == null)
            .Select(row => new { row.Category, row.Channel, row.Enabled })
            .ToListAsync(ct);

        var lookup = rows.ToDictionary(row => (row.Category, row.Channel), row => row.Enabled);
        var available = await AvailableChannelsAsync(businessId, ct);

        var preferences = new List<BusinessNotificationPreferenceDto>();
        foreach (var category in NotificationDefaults.AllCategories)
        {
            foreach (var channel in NotificationDefaults.AllChannels)
            {
                var hasOverride = lookup.TryGetValue((category, channel), out var overridden);
                preferences.Add(new BusinessNotificationPreferenceDto
                {
                    Category = category,
                    Channel = channel,
                    Enabled = hasOverride ? overridden : NotificationDefaults.IsEnabled(category, channel),
                    Available = available.Contains(channel),
                    Source = hasOverride ? PreferenceSource.Business : PreferenceSource.System,
                    AdminOverride = hasOverride && !overridden,
                });
            }
        }

        return preferences;
    }

    /// <summary>Replaces the business's (category × channel) kill-switch rows.</summary>
    public async Task<List<BusinessNotificationPreferenceDto>> UpdatePreferencesAsync(
        Guid businessId,
        UpdateBusinessNotificationPreferencesRequest request,
        Guid? actorUserId,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        foreach (var item in request.Preferences)
        {
            if (!NotificationDefaults.AllCategories.Contains(item.Category)) continue;
            if (!NotificationDefaults.AllChannels.Contains(item.Channel)) continue;

            var existing = await _context.NotificationPreferences
                .FirstOrDefaultAsync(row => row.BusinessId == businessId
                    && row.UserId == null
                    && row.Category == item.Category
                    && row.Channel == item.Channel, ct);

            var isDefault = NotificationDefaults.IsEnabled(item.Category, item.Channel);

            if (item.Enabled == isDefault)
            {
                // The table is deliberately sparse: a value equal to the system
                // default is represented by the absence of a row.
                if (existing is not null) _context.NotificationPreferences.Remove(existing);
                continue;
            }

            if (existing is null)
            {
                _context.NotificationPreferences.Add(new NotificationPreference
                {
                    Id = Guid.NewGuid(),
                    BusinessId = businessId,
                    UserId = null,
                    Category = item.Category,
                    Channel = item.Channel,
                    Enabled = item.Enabled,
                    UpdatedAt = now,
                    CreatedAt = now,
                });
            }
            else
            {
                existing.Enabled = item.Enabled;
                existing.UpdatedAt = now;
            }
        }

        await _context.SaveChangesAsync(ct);

        await _audit.RecordAsync(
            "NOTIFICATION_PREFERENCES_UPDATED",
            actorUserId,
            businessId,
            targetPlanId: null,
            payloadJson: System.Text.Json.JsonSerializer.Serialize(
                request.Preferences.Select(item => new
                {
                    item.Category,
                    item.Channel,
                    item.Enabled,
                })),
            reason: "Business notification preferences updated.");

        return await GetPreferencesAsync(businessId, ct);
    }

    // ══════════════════════════════════════════════════════════════════
    //  Templates + variables
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// The template catalogue for this business: every registered (type, channel)
    /// pair with whether a business override is in force and whether the platform
    /// protects the message from customisation.
    /// </summary>
    public async Task<List<BusinessNotificationTemplateDto>> GetTemplatesAsync(
        Guid businessId,
        string? search,
        string? category,
        string? channel,
        CancellationToken ct = default)
    {
        var overrides = await _context.NotificationTemplateOverrides
            .AsNoTracking()
            .Where(row => row.BusinessId == businessId)
            .Select(row => new { row.Id, row.Type, row.Channel, row.Version, row.UpdatedAt, row.CreatedAt })
            .ToListAsync(ct);

        var byKey = overrides.ToDictionary(row => (row.Type, row.Channel));

        var templates = new List<BusinessNotificationTemplateDto>();
        foreach (var definition in NotificationTypes.All)
        {
            if (!string.IsNullOrWhiteSpace(category)
                && !string.Equals(definition.Category, category, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var pairChannel in definition.DefaultChannels)
            {
                if (!string.IsNullOrWhiteSpace(channel)
                    && !string.Equals(pairChannel, channel, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!NotificationTemplates.TryGet(definition.Name, pairChannel, out var platform))
                    continue;

                byKey.TryGetValue((definition.Name, pairChannel), out var row);

                var dto = new BusinessNotificationTemplateDto
                {
                    Type = definition.Name,
                    Category = definition.Category,
                    Channel = pairChannel,
                    Title = platform.Title,
                    Source = row is null ? EffectiveTemplateSource.Default : EffectiveTemplateSource.Override,
                    OverrideId = row?.Id,
                    Version = row?.Version,
                    Protected = definition.IsSecurity,
                    UpdatedAtUtc = row is null ? null : row.UpdatedAt ?? row.CreatedAt,
                    AvailableVariables = NotificationManagementShared.AvailableTokens(definition),
                };

                if (!string.IsNullOrWhiteSpace(search))
                {
                    var needle = search.Trim();
                    if (!dto.Type.Contains(needle, StringComparison.OrdinalIgnoreCase)
                        && !dto.Title.Contains(needle, StringComparison.OrdinalIgnoreCase)
                        && !dto.Category.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }

                templates.Add(dto);
            }
        }

        return templates
            .OrderBy(t => t.Category, StringComparer.Ordinal)
            .ThenBy(t => t.Type, StringComparer.Ordinal)
            .ThenBy(t => t.Channel, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The variables a business may insert for one notification type.</summary>
    public IReadOnlyList<BusinessNotificationVariableDto> GetVariables(string type)
    {
        var definition = NotificationTypes.Get(type);
        return NotificationManagementShared.AvailableTokens(definition)
            .Select(NotificationManagementShared.DescribeToken)
            .ToList();
    }

    // ══════════════════════════════════════════════════════════════════
    //  Preview + test send
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Renders a template draft with realistic sample data. Unknown tokens are
    /// reported rather than silently dropped, so the editor can block a save that
    /// would send a half-rendered message.
    /// </summary>
    public async Task<PreviewTemplateResponse> PreviewAsync(
        Guid businessId,
        BusinessNotificationPreviewRequest request,
        CancellationToken ct = default)
    {
        var definition = NotificationTypes.Get(request.Type);
        var payload = NotificationManagementShared.SamplePayload(definition);
        if (request.Payload is not null)
        {
            foreach (var pair in request.Payload) payload[pair.Key] = pair.Value;
        }

        // Business branding is part of the preview: the customer sees the business's
        // own name and colours, not the generic sample values.
        var businessName = await _context.Businesses
            .AsNoTracking()
            .Where(entity => entity.Id == businessId)
            .Select(entity => entity.Name)
            .FirstOrDefaultAsync(ct);

        if (!string.IsNullOrWhiteSpace(businessName)) payload["businessName"] = businessName;

        var subjectTemplate = request.SubjectTemplate ?? string.Empty;
        var htmlTemplate = request.BodyTemplateHtml;
        var textTemplate = string.IsNullOrWhiteSpace(request.BodyTemplateText)
            ? request.BodyTemplateHtml
            : request.BodyTemplateText;

        var allowed = NotificationManagementShared.AvailableTokens(definition)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unknown = CollectTokens(subjectTemplate)
            .Concat(CollectTokens(htmlTemplate))
            .Concat(CollectTokens(textTemplate))
            .Where(token => !allowed.Contains(token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var response = new PreviewTemplateResponse
        {
            Subject = _renderer.RenderText(subjectTemplate, payload),
            Title = _renderer.RenderText(definition.TitleTemplate, payload),
            Body = _renderer.RenderText(textTemplate, payload),
            BodyHtml = _renderer.RenderHtml(htmlTemplate, payload),
            UnknownTokens = unknown,
        };

        if (string.Equals(request.Channel, NotificationChannel.Email, StringComparison.Ordinal))
        {
            var profile = await _context.BusinessMessagingProfiles
                .AsNoTracking()
                .FirstOrDefaultAsync(entity => entity.BusinessId == businessId, ct);

            var title = string.IsNullOrWhiteSpace(response.Title) ? string.Empty : response.Title;
            var content = string.IsNullOrWhiteSpace(title)
                ? response.BodyHtml
                : $"<h1>{System.Net.WebUtility.HtmlEncode(title)}</h1>{response.BodyHtml}";

            response.BodyHtml = _layout.Render(profile, response.Subject, content);
        }

        return response;
    }

    private static IEnumerable<string> CollectTokens(string? template)
    {
        if (string.IsNullOrWhiteSpace(template)) yield break;

        foreach (System.Text.RegularExpressions.Match match in
                 System.Text.RegularExpressions.Regex.Matches(
                     template, @"\{\{([A-Za-z][A-Za-z0-9_]*)\}\}"))
        {
            yield return match.Groups[1].Value;
        }
    }


    /// <summary>
    /// Sends a one-off test through the requested channel to an operator-supplied
    /// address. The message is marked as a test, never triggers a business workflow,
    /// and is recorded on a separate ledger key so it cannot inflate production metrics.
    /// </summary>
    public async Task<BusinessNotificationTestSendResponse> TestSendAsync(
        Guid businessId,
        BusinessNotificationTestSendRequest request,
        Guid? actorUserId,
        CancellationToken ct = default)
    {
        var channel = (request.Channel ?? string.Empty).Trim().ToLowerInvariant();
        if (!NotificationDefaults.AllChannels.Contains(channel))
        {
            return new BusinessNotificationTestSendResponse
            {
                Channel = channel,
                Accepted = false,
                Message = $"'{request.Channel}' is not a supported channel.",
            };
        }

        // Throws for an unregistered type; the controller maps that to a 400.
        var definition = NotificationTypes.Get(request.Type);

        var recipient = (request.Recipient ?? string.Empty).Trim();
        if (recipient.Length == 0)
        {
            return new BusinessNotificationTestSendResponse
            {
                Channel = channel,
                Accepted = false,
                Message = "A test recipient is required.",
            };
        }

        var available = await AvailableChannelsAsync(businessId, ct);
        if (!available.Contains(channel))
        {
            return new BusinessNotificationTestSendResponse
            {
                Channel = channel,
                Accepted = false,
                Message = "This channel is not available on this deployment, so no test can be delivered.",
            };
        }

        var effective = await _templates.ResolveAsync(businessId, request.Type, channel, ct);
        if (effective is null)
        {
            return new BusinessNotificationTestSendResponse
            {
                Channel = channel,
                Accepted = false,
                Message = $"No template is registered for {request.Type} on {channel}.",
            };
        }

        var payload = NotificationManagementShared.SamplePayload(definition);
        var subject = _renderer.RenderText(effective.Subject ?? effective.Title, payload);
        var body = _renderer.RenderText(effective.Body, payload);

        var profile = await _context.BusinessMessagingProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(entity => entity.BusinessId == businessId, ct);

        var (delivered, message) = await DeliverTestAsync(channel, recipient, subject, body, profile, ct);
        await RecordTestAsync(businessId, channel, request.Type, actorUserId, delivered, recipient, ct);

        return new BusinessNotificationTestSendResponse
        {
            Accepted = delivered,
            Channel = channel,
            Message = message,
            RenderedSubject = subject,
            RenderedBody = body,
        };
    }


    /// <summary>Delivers one test message through the channel's own provider path.</summary>
    private async Task<(bool Delivered, string Message)> DeliverTestAsync(
        string channel,
        string recipient,
        string subject,
        string body,
        BusinessMessagingProfile? profile,
        CancellationToken ct)
    {
        switch (channel)
        {
            case NotificationChannel.Email:
                if (!_email.Enabled || string.IsNullOrWhiteSpace(_email.FromAddress))
                    return (false, "Email delivery is not configured on this deployment.");

                try
                {
                    var mime = new MimeMessage();
                    mime.From.Add(new MailboxAddress(
                        string.IsNullOrWhiteSpace(profile?.DisplayName) ? _email.FromName : profile!.DisplayName,
                        _email.FromAddress));
                    mime.To.Add(MailboxAddress.Parse(recipient));
                    mime.Subject = $"[TEST] {subject}";

                    if (!string.IsNullOrWhiteSpace(profile?.ReplyTo))
                        mime.ReplyTo.Add(MailboxAddress.Parse(profile!.ReplyTo!));

                    var html = _layout.Render(
                        profile, mime.Subject, $"<p>{System.Net.WebUtility.HtmlEncode(body)}</p>");
                    mime.Body = new BodyBuilder
                    {
                        TextBody = $"[TEST] {body}",
                        HtmlBody = html,
                    }.ToMessageBody();

                    await _smtpDelivery.SendAsync(mime, ct);
                    return (true, $"Test email sent to {NotificationManagementShared.MaskEmail(recipient)}.");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Business test email failed for recipient.");
                    return (false, $"Test email failed ({ex.GetType().Name}).");
                }

            case NotificationChannel.Sms:
                try
                {
                    await _smsClient.SendAsync(recipient, $"[TEST] {body}", ct);
                    return (true, $"Test SMS sent to {NotificationManagementShared.MaskPhone(recipient)}.");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Business test SMS failed for recipient.");
                    return (false, $"Test SMS failed ({ex.GetType().Name}).");
                }

            case NotificationChannel.Push:
                return (false,
                    "Push tests require a subscribed device. Send a real notification to a staff member " +
                    "who has enabled push notifications.");

            default:
                return (false,
                    "In-app tests are delivered to a user's inbox. Send a real notification to a staff " +
                    "member to test the in-app experience.");
        }
    }


    /// <summary>
    /// Records a test send on its own ledger key. Test rows are excluded from the
    /// business overview and the admin analytics, so a test can never be mistaken
    /// for production delivery.
    /// </summary>
    private async Task RecordTestAsync(
        Guid businessId,
        string channel,
        string type,
        Guid? actorUserId,
        bool delivered,
        string recipient,
        CancellationToken ct)
    {
        var ownerId = await _context.BusinessMemberships
            .AsNoTracking()
            .Where(membership => membership.BusinessId == businessId
                && membership.Status == BusinessMembershipStatus.Active)
            .OrderBy(membership => membership.CreatedAt)
            .Select(membership => (Guid?)membership.UserId)
            .FirstOrDefaultAsync(ct);

        var now = DateTime.UtcNow;
        _context.NotificationLogs.Add(new NotificationLog
        {
            Id = Guid.NewGuid(),
            UserId = actorUserId ?? ownerId ?? Guid.Empty,
            BusinessId = businessId,
            Channel = channel,
            TemplateType = type,
            TemplateKey = TestTemplateKeyPrefix + channel,
            Status = delivered ? "sent" : "failed",
            SentAt = now,
            DeliveredAt = delivered ? now : null,
            NextAttemptAt = now,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                __test = true,
                recipient = NotificationManagementShared.MaskRecipient(
                    channel == NotificationChannel.Email ? recipient : null,
                    channel == NotificationChannel.Sms ? recipient : null,
                    Guid.Empty),
            }),
            Error = delivered ? null : "test:delivery_failed",
            Attempts = 1,
            CreatedAt = now,
            UpdatedAt = now,
        });

        await _context.SaveChangesAsync(ct);
    }

    /// <summary>A ledger row flattened for the overview aggregation.</summary>
    private readonly record struct OverviewRow(
        Guid Id,
        Guid UserId,
        string Channel,
        string TemplateType,
        string Status,
        DateTime CreatedAt,
        DateTime? DeliveredAt);
}

