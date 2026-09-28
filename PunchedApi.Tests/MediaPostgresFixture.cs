using Microsoft.EntityFrameworkCore;
using PunchedApi.Infrastructure.Data;
using Testcontainers.PostgreSql;

namespace PunchedApi.Tests;

public sealed class MediaPostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("punched_media_tests")
        .WithUsername("postgres")
        .WithPassword("media_test_only")
        .Build();

    public string ConnectionString => _postgres.GetConnectionString();

    public async Task InitializeAsync()
    {
        if (!DockerProbe.IsAvailable) return;
        await _postgres.StartAsync();
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(ConnectionString).Options;
        return new ApplicationDbContext(options);
    }

    public async Task DisposeAsync()
    {
        if (DockerProbe.IsAvailable) await _postgres.DisposeAsync();
    }
}
