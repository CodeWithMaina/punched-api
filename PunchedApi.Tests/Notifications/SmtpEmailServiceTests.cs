using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PunchedApi.Application.Settings;
using PunchedApi.Infrastructure.Data;
using PunchedApi.Infrastructure.Services;

namespace PunchedApi.Tests.Notifications;

/// <summary>
/// The direct transactional path must fail closed and loud when email is not
/// configured, instead of opening an SMTP session against an empty host.
/// </summary>
public class SmtpEmailServiceTests
{
    [Fact]
    public async Task Disabled_email_is_refused_without_attempting_smtp()
    {
        await using var provider = BuildProvider();
        var service = CreateService(provider, new EmailSettings
        {
            Enabled = false,
            Host = "smtp.example.com",
            FromAddress = "from@example.com",
            Username = "user@example.com"
        });

        var sent = await service.SendVerificationCodeAsync("recipient@example.com", "123456");

        Assert.False(sent);
    }

    [Fact]
    public async Task Missing_host_is_refused_without_attempting_smtp()
    {
        await using var provider = BuildProvider();
        var service = CreateService(provider, new EmailSettings
        {
            Enabled = true,
            Host = string.Empty,
            FromAddress = "from@example.com"
        });

        var sent = await service.SendPasswordResetCodeAsync("recipient@example.com", "654321");

        Assert.False(sent);
    }

    [Fact]
    public async Task Missing_sender_is_refused_without_attempting_smtp()
    {
        await using var provider = BuildProvider();
        var service = CreateService(provider, new EmailSettings
        {
            Enabled = true,
            Host = "smtp.example.com",
            FromAddress = string.Empty,
            Username = string.Empty
        });

        var sent = await service.SendWelcomeAsync("recipient@example.com", "Test");

        Assert.False(sent);
    }

    private static SmtpEmailService CreateService(IServiceProvider provider, EmailSettings settings) => new(
        Options.Create(settings),
        TestHelpers.CreateLogger<SmtpEmailService>(),
        provider.GetRequiredService<IServiceScopeFactory>());

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseInMemoryDatabase(Guid.NewGuid().ToString("N")));
        return services.BuildServiceProvider();
    }
}
