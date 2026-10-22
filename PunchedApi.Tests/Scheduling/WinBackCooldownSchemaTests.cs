using Microsoft.EntityFrameworkCore;
using Npgsql;
using PunchedApi.Application.Scheduling;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;
using Testcontainers.PostgreSql;

namespace PunchedApi.Tests.Scheduling;

/// <summary>
/// The win-back cooldown reads the newest successfully sent nudge per
/// (business, customer) on every scan, so the supporting index must really be
/// created by the migration chain on PostgreSQL — SQLite cannot express the
/// partial-index semantics asserted here.
/// </summary>
public class WinBackCooldownSchemaTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("punched_winback_tests")
        .WithUsername("postgres")
        .WithPassword("winback_test_only")
        .Build();

    private string ConnectionString => _postgres.GetConnectionString();

    public async Task InitializeAsync()
    {
        if (!DockerProbe.IsAvailable) return;
        await _postgres.StartAsync();
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (DockerProbe.IsAvailable) await _postgres.DisposeAsync();
    }

    [RequiresDockerFact]
    public async Task Migration_creates_the_partial_sent_only_cooldown_index()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            "SELECT indexdef FROM pg_indexes WHERE indexname = 'ix_notifications_winback_cooldown'",
            connection);
        var definition = (string?)await command.ExecuteScalarAsync();

        Assert.NotNull(definition);
        Assert.Contains("business_id", definition);
        Assert.Contains("user_id", definition);
        Assert.Contains("template_type", definition);
        Assert.Contains("status", definition);
        Assert.Contains("sent_at", definition);
        Assert.Contains("WHERE", definition);
        Assert.Contains("'sent'", definition);
    }

    [RequiresDockerFact]
    public async Task Migration_is_recorded_so_it_only_runs_once()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '20261201000000_AddWinBackCooldownIndex'",
            connection);
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }

    [RequiresDockerFact]
    public async Task Cooldown_reads_only_sent_win_back_rows_for_the_pair()
    {
        await using var db = CreateContext();
        var owner = BookingTestBase.CreateOwner($"owner-{Guid.NewGuid():N}@test.com");
        var customer = BookingTestBase.CreateCustomer($"customer-{Guid.NewGuid():N}@test.com");
        db.Users.AddRange(owner, customer);
        var business = BookingTestBase.CreateBusiness(owner.Id, "Win-back Salon");
        db.Businesses.Add(business);

        var now = DateTime.UtcNow;
        var nudgedAt = now.AddDays(-10);
        db.NotificationLogs.AddRange(
            // The only row that starts a cooldown.
            NewLog(customer.Id, business.Id, "loyalty.win_back_nudge", "sent", nudgedAt),
            // Transactional, newer — must not extend the cooldown.
            NewLog(customer.Id, business.Id, "appointment.booked", "sent", now.AddDays(-1)),
            // Queued, not yet sent — must not start one either.
            NewLog(customer.Id, business.Id, "customer.lapsed", "pending", now.AddDays(-2)));
        await db.SaveChangesAsync();

        var cooldown = new WinBackCooldownService(db);
        var lastSentAt = await cooldown.GetLastSentAtAsync(customer.Id, business.Id);
        var cooldownEndsAt = await cooldown.GetCooldownEndsAtAsync(customer.Id, business.Id, 30);

        Assert.NotNull(lastSentAt);
        Assert.Equal(nudgedAt, lastSentAt!.Value, TimeSpan.FromSeconds(1));
        Assert.NotNull(cooldownEndsAt);
        Assert.Equal(nudgedAt.AddDays(30), cooldownEndsAt!.Value, TimeSpan.FromSeconds(1));
        Assert.True(cooldownEndsAt > now);
    }

    private static NotificationLog NewLog(
        Guid customerId, Guid businessId, string templateType, string status, DateTime sentAt)
        => new()
        {
            Id = Guid.NewGuid(),
            UserId = customerId,
            BusinessId = businessId,
            Channel = "in_app",
            TemplateType = templateType,
            Status = status,
            SentAt = sentAt,
            CreatedAt = sentAt,
            NextAttemptAt = sentAt,
            UpdatedAt = sentAt,
        };

    private ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(ConnectionString).Options;
        return new ApplicationDbContext(options);
    }
}
