using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PunchedApi.Application.Notifications;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;
using PunchedApi.Infrastructure.Notifications;
using Testcontainers.PostgreSql;

namespace PunchedApi.Tests.Notifications;

public sealed class NotificationOutboxPostgresFixture : IAsyncLifetime
{
    private const string EnableVariable = "PUNCHED_NOTIFICATION_POSTGRES_TESTS";
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("punched_notification_tests")
        .WithUsername("postgres")
        .WithPassword("notification_test_only")
        .Build();
    private bool _started;

    public string ConnectionString => _postgres.GetConnectionString();

    public async Task InitializeAsync()
    {
        if (!IsEnabled || !DockerProbe.IsAvailable) return;
        await _postgres.StartAsync();
        _started = true;
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(ConnectionString).Options;
        return new ApplicationDbContext(options);
    }

    public static bool IsEnabled => string.Equals(
        Environment.GetEnvironmentVariable(EnableVariable), "on", StringComparison.OrdinalIgnoreCase);

    public async Task DisposeAsync()
    {
        if (_started) await _postgres.DisposeAsync();
    }
}

public sealed class RequiresNotificationPostgresFactAttribute : FactAttribute
{
    public RequiresNotificationPostgresFactAttribute()
    {
        if (!NotificationOutboxPostgresFixture.IsEnabled)
            Skip = "Set PUNCHED_NOTIFICATION_POSTGRES_TESTS=on to run the isolated PostgreSQL outbox test.";
        else if (!DockerProbe.IsAvailable)
            Skip = DockerProbe.SkipReason;
    }
}

public sealed class NotificationOutboxPostgresTests : IClassFixture<NotificationOutboxPostgresFixture>
{
    private readonly NotificationOutboxPostgresFixture _fixture;

    public NotificationOutboxPostgresTests(NotificationOutboxPostgresFixture fixture) => _fixture = fixture;

    [RequiresNotificationPostgresFact]
    public async Task Two_workers_claim_more_than_one_batch_without_duplicate_delivery()
    {
        var user = BookingTestBase.CreateCustomer("notification-race@test.com");
        const int rowCount = 101;
        await using (var seed = _fixture.CreateContext())
        {
            seed.Users.Add(user);
            seed.NotificationLogs.AddRange(Enumerable.Range(0, rowCount).Select(index => new NotificationLog
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                Channel = NotificationChannel.Email,
                TemplateType = "appointment.booked",
                Status = "pending",
                PayloadJson = "{}",
                Attempts = 0,
                NextAttemptAt = DateTime.UtcNow.AddMinutes(-1),
                IdempotencyKey = $"postgres-race:{Guid.NewGuid():N}:{index}",
                UpdatedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow.AddMilliseconds(index)
            }));
            await seed.SaveChangesAsync();
        }

        var channel = new TestNotificationChannel();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(options => options.UseNpgsql(_fixture.ConnectionString));
        services.AddScoped<NotificationOutboxStore>();
        services.AddScoped<ITemplateRenderer, TemplateRenderer>();
        services.AddSingleton<INotificationChannel>(channel);
        await using var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var logger = provider.GetRequiredService<ILogger<NotificationDeliveryWorker>>();
        var firstWorker = new NotificationDeliveryWorker(scopeFactory, logger);
        var secondWorker = new NotificationDeliveryWorker(scopeFactory, logger);

        static async Task DrainAsync(NotificationDeliveryWorker worker)
        {
            while (await worker.ProcessBatchAsync() > 0) { }
        }

        await Task.WhenAll(DrainAsync(firstWorker), DrainAsync(secondWorker));

        await using var verify = _fixture.CreateContext();
        var rows = await verify.NotificationLogs.AsNoTracking().ToListAsync();
        var deliveredIds = channel.Messages.Select(message => message.OutboxId).ToArray();
        Assert.Equal(rowCount, rows.Count);
        Assert.All(rows, row => Assert.Equal("sent", row.Status));
        Assert.Equal(rowCount, channel.Calls);
        Assert.Equal(rowCount, deliveredIds.Length);
        Assert.Equal(rowCount, deliveredIds.Distinct().Count());
    }

    [RequiresNotificationPostgresFact]
    public async Task Stale_processing_rows_are_reclaimed_to_pending_for_redelivery()
    {
        var user = BookingTestBase.CreateCustomer("notification-reclaim@test.com");
        var staleId = Guid.NewGuid();

        await using (var seed = _fixture.CreateContext())
        {
            seed.Users.Add(user);
            seed.NotificationLogs.Add(new NotificationLog
            {
                Id = staleId,
                UserId = user.Id,
                Channel = NotificationChannel.Email,
                TemplateType = "appointment.booked",
                Status = "processing",
                PayloadJson = "{}",
                Attempts = 1,
                NextAttemptAt = DateTime.UtcNow.AddMinutes(-1),
                IdempotencyKey = $"postgres-reclaim:{staleId:N}",
                UpdatedAt = DateTime.UtcNow.AddMinutes(-10),
                CreatedAt = DateTime.UtcNow.AddMinutes(-11)
            });
            await seed.SaveChangesAsync();
        }

        await using var context = _fixture.CreateContext();
        var store = new NotificationOutboxStore(context);
        var reclaimed = await store.ReclaimStaleProcessingAsync(TimeSpan.FromMinutes(5));

        Assert.Equal(1, reclaimed);

        var row = await context.NotificationLogs.SingleAsync(item => item.Id == staleId);
        Assert.Equal("pending", row.Status);
        Assert.True(row.NextAttemptAt <= DateTime.UtcNow);

        await using var cleanup = _fixture.CreateContext();
        await cleanup.NotificationLogs.Where(item => item.Id == staleId).ExecuteDeleteAsync();
    }
}