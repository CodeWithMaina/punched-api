using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PunchedApi.Application.Notifications;
using PunchedApi.Application.Settings;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;
using PunchedApi.Infrastructure.Repositories;

namespace PunchedApi.Tests.Notifications;

/// <summary>
/// Availability is provider configuration plus recipient eligibility — never a
/// user preference. It is what stops the SECURITY Force path (and the preference
/// view) from advertising or queuing a channel the deployment cannot deliver on.
/// </summary>
public class PreferenceResolverAvailabilityTests
{
    [Fact]
    public async Task Preference_view_offers_email_only_for_a_verified_address()
    {
        await using var context = CreateContext();
        var verified = CreateUser("verified@test.com", verified: true);
        var unverified = CreateUser("unverified@test.com", verified: false);
        context.Users.AddRange(verified, unverified);
        await context.SaveChangesAsync();

        var resolver = CreateResolver(context, email: new EmailSettings
        {
            Enabled = true,
            Host = "smtp.example.com",
            FromAddress = "from@example.com"
        });

        // The preference view only offers email once the address is proven.
        Assert.True((await resolver.GetResolvedAsync(verified.Id, null))
            .First(preference => preference.Channel == NotificationChannel.Email).Available);
        Assert.False((await resolver.GetResolvedAsync(unverified.Id, null))
            .First(preference => preference.Channel == NotificationChannel.Email).Available);

        // Configuration + eligibility still narrows the dispatch set: push is
        // unconfigured here, so it is dropped even for the verified recipient.
        Assert.Equal(
            new[] { NotificationChannel.InApp, NotificationChannel.Email },
            await resolver.FilterAvailableAsync(verified.Id, new[] { "in_app", "email", "sms", "push" }));
    }

    [Fact]
    public async Task Push_is_available_only_when_vapid_is_fully_configured()
    {
        await using var context = CreateContext();
        var userId = Guid.NewGuid();

        var configured = CreateResolver(context, vapid: new VapidSettings
        {
            Enabled = true,
            PublicKey = "public",
            PrivateKey = "private"
        });
        Assert.Contains(
            NotificationChannel.Push,
            await configured.FilterAvailableAsync(userId, new[] { "push" }));

        var missingKeys = CreateResolver(context, vapid: new VapidSettings
        {
            Enabled = true,
            PublicKey = string.Empty,
            PrivateKey = string.Empty
        });
        Assert.DoesNotContain(
            NotificationChannel.Push,
            await missingKeys.FilterAvailableAsync(userId, new[] { "push" }));
    }

    [Fact]
    public async Task Filter_available_offers_email_before_the_address_is_verified()
    {
        await using var context = CreateContext();
        var unverified = CreateUser("unverified@test.com", verified: false);
        context.Users.Add(unverified);
        await context.SaveChangesAsync();

        var resolver = CreateResolver(context, email: new EmailSettings
        {
            Enabled = true,
            Host = "smtp.example.com",
            FromAddress = "from@example.com"
        });

        // Dispatch path: email must be offered even before verification, because the
        // verification code is what proves the address.
        Assert.Contains(
            NotificationChannel.Email,
            await resolver.FilterAvailableAsync(unverified.Id, new[] { "in_app", "email" }));

        // Preference view: still withheld until the address is proven.
        var resolved = await resolver.GetResolvedAsync(unverified.Id, null);
        Assert.False(resolved.First(preference => preference.Channel == NotificationChannel.Email).Available);
    }

    [Fact]
    public async Task Filter_preserves_order_and_drops_duplicates()
    {
        await using var context = CreateContext();
        var resolver = CreateResolver(context);

        var filtered = await resolver.FilterAvailableAsync(
            Guid.NewGuid(), new[] { "in_app", "in_app", "push", "email", "sms" });

        Assert.Equal(new[] { NotificationChannel.InApp }, filtered);
    }

    private static ApplicationDbContext CreateContext() => new(
        new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private static PreferenceResolver CreateResolver(
        ApplicationDbContext context,
        EmailSettings? email = null,
        SmsSettings? sms = null,
        VapidSettings? vapid = null) => new(
        new UnitOfWork(context),
        new MemoryCache(new MemoryCacheOptions()),
        Array.Empty<INotificationChannel>(),
        Options.Create(email ?? new EmailSettings { Enabled = false }),
        Options.Create(sms ?? new SmsSettings { Enabled = false }),
        Options.Create(vapid ?? new VapidSettings { Enabled = false }));

    private static User CreateUser(string email, bool verified) => new()
    {
        Id = Guid.NewGuid(),
        Email = email,
        FullName = "Test Recipient",
        Role = UserRole.Customer,
        Auth = new UserAuth
        {
            Id = Guid.NewGuid(),
            Email = email,
            PasswordHash = "hash",
            IsVerified = verified
        }
    };
}
