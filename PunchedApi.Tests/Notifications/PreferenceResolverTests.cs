using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using PunchedApi.Application.Notifications;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;
using PunchedApi.Infrastructure.Repositories;

namespace PunchedApi.Tests.Notifications;

public class PreferenceResolverTests
{
    [Fact]
    public async Task Business_false_suppresses_external_channel() =>
        Assert.False(await ResolveAsync(NotificationChannel.Email, businessEnabled: false));

    [Fact]
    public async Task Business_true_does_not_override_user_false() =>
        Assert.False(await ResolveAsync(NotificationChannel.Email, businessEnabled: true, userEnabled: false, scoped: true));

    [Fact]
    public async Task User_business_row_beats_global_row() =>
        Assert.True(await ResolveAsync(NotificationChannel.Email, userEnabled: true, globalEnabled: false, scoped: true));

    [Fact]
    public async Task Global_user_row_is_fallback() =>
        Assert.False(await ResolveAsync(NotificationChannel.Email, userEnabled: false));

    [Fact]
    public void Marketing_defaults_off() => Assert.False(NotificationDefaults.IsEnabled(NotificationCategory.Marketing, NotificationChannel.Email));

    [Fact]
    public async Task Business_kill_switch_cannot_disable_in_app() =>
        Assert.True(await ResolveAsync(NotificationChannel.InApp, businessEnabled: false));

    [Fact]
    public async Task Default_value_is_deleted_to_keep_preferences_sparse()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.AddAsync(fixture.UserId, null, false);

        var changed = await fixture.Resolver.UpsertAsync(fixture.UserId, null, new[]
        {
            new Application.DTOs.NotificationPreferenceItem
            {
                Category = NotificationCategory.Loyalty,
                Channel = NotificationChannel.Email,
                Enabled = NotificationDefaults.IsEnabled(NotificationCategory.Loyalty, NotificationChannel.Email)
            }
        });

        Assert.Equal(1, changed);
        Assert.Empty(await fixture.Context.NotificationPreferences.ToListAsync());
    }

    private static async Task<bool> ResolveAsync(
        string channel,
        bool? businessEnabled = null,
        bool? userEnabled = null,
        bool? globalEnabled = null,
        bool scoped = false)
    {
        await using var fixture = await Fixture.CreateAsync();
        if (businessEnabled.HasValue)
            await fixture.AddAsync(userId: null, businessId: fixture.BusinessId, enabled: businessEnabled.Value);
        if (userEnabled.HasValue)
            await fixture.AddAsync(fixture.UserId, scoped ? fixture.BusinessId : null, userEnabled.Value);
        if (globalEnabled.HasValue)
            await fixture.AddAsync(fixture.UserId, null, globalEnabled.Value);
        return (await fixture.Resolver.ResolveAsync(
            fixture.UserId, fixture.BusinessId, NotificationCategory.Loyalty, new[] { channel })).Contains(channel);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public Guid UserId { get; } = Guid.NewGuid();
        public Guid BusinessId { get; } = Guid.NewGuid();
        public ApplicationDbContext Context { get; }
        public PreferenceResolver Resolver { get; }

        private Fixture(ApplicationDbContext context, UnitOfWork unitOfWork)
        {
            Context = context;
            Resolver = new PreferenceResolver(unitOfWork, new MemoryCache(new MemoryCacheOptions()), Array.Empty<INotificationChannel>());
        }

        public static async Task<Fixture> CreateAsync()
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;
            var context = new ApplicationDbContext(options);
            await context.Database.EnsureCreatedAsync();
            return new Fixture(context, new UnitOfWork(context));
        }

        public async Task AddAsync(Guid? userId, Guid? businessId, bool enabled)
        {
            Context.NotificationPreferences.Add(new NotificationPreference
            {
                Id = Guid.NewGuid(), UserId = userId, BusinessId = businessId,
                Category = NotificationCategory.Loyalty, Channel = NotificationChannel.Email,
                Enabled = enabled
            });
            await Context.SaveChangesAsync();
        }

        public ValueTask DisposeAsync()
        {
            Context.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}