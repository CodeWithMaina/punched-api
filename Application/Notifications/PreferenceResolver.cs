using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Settings;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;

namespace PunchedApi.Application.Notifications;

/// <summary>
/// Pure decision: system &gt; business &gt; user (architecture doc §7).
/// </summary>
/// <remarks>
/// <para>Resolution order for <c>(user, business, category, channel)</c>:</para>
/// <list type="number">
///   <item>the business kill-switch suppresses (R1) — except <c>in_app</c>, which has no kill-switch (R5);</item>
///   <item>a user row scoped to this business wins if present;</item>
///   <item>a global user row wins if present;</item>
///   <item>otherwise the code default applies (R4 — absence *is* inherit, which keeps the table sparse).</item>
/// </list>
/// <para>
/// Override rows are cached in <see cref="IMemoryCache"/> for 60 seconds — the same TTL as
/// <c>BusinessScopeResolver</c> — and every preference write evicts the affected key (R6).
/// </para>
/// </remarks>
public sealed class PreferenceResolver : IPreferenceResolver
{
    private const string ScopeUserGlobal = "user_global";
    private const string ScopeUserBusiness = "user_business";
    private const string ScopeBusiness = "business";

    private const string UserKeyPrefix = "notifpref:user:";
    private const string BusinessKeyPrefix = "notifpref:biz:";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private readonly IUnitOfWork _unitOfWork;
    private readonly IMemoryCache _cache;
    private readonly IReadOnlyList<INotificationChannel> _channels;
    private readonly EmailSettings _emailSettings;
    private readonly SmsSettings _smsSettings;
    private readonly VapidSettings _vapidSettings;
    private readonly bool _hasConfiguredSettings;

    public PreferenceResolver(
        IUnitOfWork unitOfWork,
        IMemoryCache cache,
        IEnumerable<INotificationChannel> channels,
        IOptions<EmailSettings>? emailSettings = null,
        IOptions<SmsSettings>? smsSettings = null,
        IOptions<VapidSettings>? vapidSettings = null)
    {
        _unitOfWork = unitOfWork;
        _cache = cache;
        _channels = channels.ToList();
        _emailSettings = emailSettings?.Value ?? new EmailSettings();
        _smsSettings = smsSettings?.Value ?? new SmsSettings();
        _vapidSettings = vapidSettings?.Value ?? new VapidSettings();
        _hasConfiguredSettings = emailSettings is not null || smsSettings is not null || vapidSettings is not null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ResolveAsync(
        Guid userId,
        Guid? businessId,
        string category,
        IEnumerable<string> candidates,
        CancellationToken ct = default)
    {
        // An unknown category is a programmer error, not a suppression.
        if (!NotificationDefaults.AllCategories.Contains(category))
            throw new ArgumentException($"Unknown notification category '{category}'.", nameof(category));

        var rows = await LoadOverridesAsync(userId, businessId, ct);
        var available = await AvailableChannelsAsync(userId, ct);

        var surviving = new List<string>();
        foreach (var channel in candidates)
        {
            if (surviving.Contains(channel)) continue;
            if (!available.Contains(channel)) continue;
            if (IsEnabled(rows, category, channel)) surviving.Add(channel);
        }

        return surviving;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ResolvedPreferenceDto>> GetResolvedAsync(
        Guid userId,
        Guid? businessId,
        CancellationToken ct = default)
    {
        var rows = await LoadOverridesAsync(userId, businessId, ct);
        var available = await AvailableChannelsAsync(userId, ct);

        var resolved = new List<ResolvedPreferenceDto>(
            NotificationDefaults.AllCategories.Count * NotificationDefaults.AllChannels.Count);

        foreach (var category in NotificationDefaults.AllCategories)
        {
            foreach (var channel in NotificationDefaults.AllChannels)
            {
                resolved.Add(new ResolvedPreferenceDto
                {
                    Category = category,
                    Channel = channel,
                    Enabled = IsEnabled(rows, category, channel),
                    Available = available.Contains(channel),
                    Source = ResolveSource(rows, category, channel)
                });
            }
        }

        return resolved;
    }

    /// <inheritdoc />
    public async Task<int> UpsertAsync(
        Guid userId,
        Guid? businessId,
        IReadOnlyList<NotificationPreferenceItem> preferences,
        CancellationToken ct = default)
    {
        // Last write wins for a repeated (category, channel) pair.
        var desired = new Dictionary<(string Category, string Channel), bool>();
        foreach (var item in preferences)
        {
            Validate(item);
            desired[(item.Category, item.Channel)] = item.Enabled;
        }

        // I5 — a business-scoped user row is only accepted for a business the
        // caller actually belongs to (owner, staff link, or enrollment). The row is
        // always the caller's own (user_id = userId, never client supplied).
        if (businessId.HasValue && !await IsAssociatedWithBusinessAsync(userId, businessId.Value))
        {
            throw new ArgumentException(
                $"User {userId} is not associated with business {businessId.Value}.",
                nameof(businessId));
        }

        var repository = _unitOfWork.NotificationPreferences;
        var existing = businessId.HasValue
            ? (await repository.FindAsync(p => p.UserId == userId && p.BusinessId == businessId.Value)).ToList()
            : (await repository.FindAsync(p => p.UserId == userId && p.BusinessId == null)).ToList();

        var changed = 0;
        var now = DateTime.UtcNow;

        foreach (var ((category, channel), enabled) in desired)
        {
            var row = existing.FirstOrDefault(p => p.Category == category && p.Channel == channel);
            var isDefault = NotificationDefaults.IsEnabled(category, channel);

            if (enabled == isDefault)
            {
                // Equal to the system default: keep the table sparse by removing the row.
                if (row != null)
                {
                    repository.Delete(row);
                    changed++;
                }

                continue;
            }

            if (row == null)
            {
                await repository.AddAsync(new NotificationPreference
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    BusinessId = businessId,
                    Category = category,
                    Channel = channel,
                    Enabled = enabled,
                    CreatedAt = now,
                    UpdatedAt = now
                });
                changed++;
                continue;
            }

            if (row.Enabled != enabled)
            {
                row.Enabled = enabled;
                row.UpdatedAt = now;
                repository.Update(row);
                changed++;
            }
        }

        if (changed > 0)
            await _unitOfWork.SaveChangesAsync();

        // R6 — a stale allow must never survive a write.
        Invalidate(userId, businessId);

        return changed;
    }

    /// <inheritdoc />
    public void Invalidate(Guid userId, Guid? businessId)
    {
        _cache.Remove(UserKeyPrefix + userId);

        if (businessId.HasValue) InvalidateBusiness(businessId.Value);
    }

    /// <summary>Evicts the business kill-switch cache entry (business settings writes — Phase 5).</summary>
    public void InvalidateBusiness(Guid businessId) =>
        _cache.Remove(BusinessKeyPrefix + businessId);

    // ── resolution ───────────────────────────────────────────────

    /// <summary>
    /// Steps 2-5 of the resolution order. Step 1 (<c>Force</c>) is not a preference
    /// concern and is handled by <see cref="NotificationService"/> (R2).
    /// </summary>
    private static bool IsEnabled(IReadOnlyList<PreferenceRow> rows, string category, string channel)
    {
        // R1 — a business can only close, never force open.
        // R5 — in_app has no kill switch.
        if (channel != NotificationChannel.InApp &&
            rows.Any(r => r.Scope == ScopeBusiness && r.Category == category && r.Channel == channel && !r.Enabled))
        {
            return false;
        }

        // Step 3 — the user's business-scoped row wins if present...
        var scoped = Find(rows, ScopeUserBusiness, category, channel);
        if (scoped != null) return scoped.Enabled;

        // ...then their global row...
        var global = Find(rows, ScopeUserGlobal, category, channel);
        if (global != null) return global.Enabled;

        // ...then the code default (R4).
        return NotificationDefaults.IsEnabled(category, channel);
    }

    /// <summary>Which level decided the value, so a client can badge "set by your business".</summary>
    private static string ResolveSource(IReadOnlyList<PreferenceRow> rows, string category, string channel)
    {
        if (channel != NotificationChannel.InApp &&
            rows.Any(r => r.Scope == ScopeBusiness && r.Category == category && r.Channel == channel && !r.Enabled))
        {
            return PreferenceSource.Business;
        }

        if (Find(rows, ScopeUserBusiness, category, channel) != null ||
            Find(rows, ScopeUserGlobal, category, channel) != null)
        {
            return PreferenceSource.User;
        }

        return PreferenceSource.System;
    }

    private static PreferenceRow? Find(
        IReadOnlyList<PreferenceRow> rows, string scope, string category, string channel)
    {
        foreach (var row in rows)
        {
            if (row.Scope == scope && row.Category == category && row.Channel == channel)
                return row;
        }

        return null;
    }

    /// <summary>
    /// Channels this deployment can actually deliver on: <c>in_app</c> plus one
    /// <see cref="INotificationChannel"/> registration per shipped external channel.
    /// The DI registration *is* the phase gate, so shipping a channel needs no edit
    /// here (email = Phase 4, sms = Phase 6, push = Phase 7).
    /// </summary>
    private async Task<HashSet<string>> AvailableChannelsAsync(Guid userId, CancellationToken ct)
    {
        var available = new HashSet<string>(StringComparer.Ordinal) { NotificationChannel.InApp };
        if (!_hasConfiguredSettings && _channels.Count == 0)
        {
            available.UnionWith(NotificationDefaults.AllChannels.Where(channel => channel != NotificationChannel.InApp));
            return available;
        }

        if ((_emailSettings.Enabled && !string.IsNullOrWhiteSpace(_emailSettings.Host)
            && !string.IsNullOrWhiteSpace(_emailSettings.FromAddress)
            && await HasVerifiedEmailAsync(userId))
            || (!_hasConfiguredSettings && _channels.Any(channel => channel.Name == NotificationChannel.Email)))
            available.Add(NotificationChannel.Email);
        if ((_smsSettings.Enabled && await HasPhoneNumberAsync(userId))
            || (!_hasConfiguredSettings && _channels.Any(channel => channel.Name == NotificationChannel.Sms)))
            available.Add(NotificationChannel.Sms);
        if ((_vapidSettings.Enabled && !string.IsNullOrWhiteSpace(_vapidSettings.PublicKey)
            && !string.IsNullOrWhiteSpace(_vapidSettings.PrivateKey))
            || (!_hasConfiguredSettings && _channels.Any(channel => channel.Name == NotificationChannel.Push)))
            available.Add(NotificationChannel.Push);
        return available;
    }

    private async Task<bool> HasVerifiedEmailAsync(Guid userId)
    {
        var user = await _unitOfWork.Users.FirstOrDefaultAsync(item => item.Id == userId);
        if (user is null) return false;
        var auth = await _unitOfWork.UserAuths.FirstOrDefaultAsync(item => item.Email == user.Email);
        return auth?.IsVerified == true;
    }

    private async Task<bool> HasPhoneNumberAsync(Guid userId)
    {
        var user = await _unitOfWork.Users.FirstOrDefaultAsync(item => item.Id == userId);
        return !string.IsNullOrWhiteSpace(user?.PhoneNumber);
    }

    private static void Validate(NotificationPreferenceItem item)
    {
        if (!NotificationDefaults.AllCategories.Contains(item.Category))
            throw new ArgumentException($"Unknown notification category '{item.Category}'.", nameof(item));

        if (!NotificationDefaults.AllChannels.Contains(item.Channel))
            throw new ArgumentException($"Unknown notification channel '{item.Channel}'.", nameof(item));
    }

    // ── cache ────────────────────────────────────────────────────

    /// <summary>
    /// Owner, staff link, or customer enrollment — the same three relationships the
    /// send path validates, so a preference row can only be written for a business
    /// the caller genuinely belongs to.
    /// </summary>
    private async Task<bool> IsAssociatedWithBusinessAsync(Guid userId, Guid businessId)
    {
        if (await _unitOfWork.Businesses.AnyAsync(b => b.Id == businessId && b.OwnerId == userId))
            return true;

        if (await _unitOfWork.Users.AnyAsync(u => u.Id == userId && u.StaffBusinessId == businessId))
            return true;

        return await _unitOfWork.CustomerBusinessEnrollments
            .AnyAsync(e => e.CustomerId == userId && e.BusinessId == businessId);
    }

    private async Task<IReadOnlyList<PreferenceRow>> LoadOverridesAsync(
        Guid userId, Guid? businessId, CancellationToken ct)
    {
        // One cache entry per user holds ALL of their rows (global + per-business),
        // so a global write correctly invalidates every business-scoped decision.
        var userRows = await _cache.GetOrCreateAsync(UserKeyPrefix + userId, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;

            var rows = await _unitOfWork.NotificationPreferences.FindAsync(p => p.UserId == userId);
            return rows.Select(p => new PreferenceRow(
                p.Category,
                p.Channel,
                p.BusinessId == null ? ScopeUserGlobal : ScopeUserBusiness,
                p.Enabled)).ToList();
        }) ?? new List<PreferenceRow>();

        if (!businessId.HasValue) return userRows;

        // The business kill-switch rows are shared by every user of the tenant.
        var businessRows = await _cache.GetOrCreateAsync(BusinessKeyPrefix + businessId.Value, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;

            var rows = await _unitOfWork.NotificationPreferences.FindAsync(
                p => p.UserId == null && p.BusinessId == businessId.Value);
            return rows.Select(p => new PreferenceRow(p.Category, p.Channel, ScopeBusiness, p.Enabled)).ToList();
        }) ?? new List<PreferenceRow>();

        if (businessRows.Count == 0) return userRows;

        var combined = new List<PreferenceRow>(userRows.Count + businessRows.Count);
        combined.AddRange(userRows);
        combined.AddRange(businessRows);
        return combined;
    }

    /// <summary>Immutable cache snapshot — never a tracked EF entity.</summary>
    private sealed record PreferenceRow(string Category, string Channel, string Scope, bool Enabled = true);
}
