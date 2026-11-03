using Microsoft.EntityFrameworkCore;
using PunchedApi.Infrastructure.Data;
using Testcontainers.PostgreSql;

namespace PunchedApi.Tests;

/// <summary>
/// PostgreSQL fixture for the appointment concurrency tests.
///
/// <para>They need the real provider because the protections under test are
/// database-level: the <c>appointments_no_staff_overlap</c> exclusion constraint and
/// the transaction-scoped advisory lock the booking paths take. SQLite has neither,
/// so it cannot model "two simultaneous requests" at all — the fixture exists so
/// that assertion is made against the engine that actually runs in production.</para>
///
/// <para>Migrations run on start, so the constraint predicate asserted here is the
/// one the migrations create rather than one hand-built for the test.</para>
/// </summary>
public sealed class AppointmentPostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("punched_appointment_tests")
        .WithUsername("postgres")
        .WithPassword("appointment_test_only")
        .Build();

    public string ConnectionString => _postgres.GetConnectionString();

    public async Task InitializeAsync()
    {
        if (!DockerProbe.IsAvailable) return;
        await _postgres.StartAsync();
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    /// <summary>A fresh context: a DbContext is not thread-safe, so each parallel request needs its own.</summary>
    public ApplicationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;
        return new ApplicationDbContext(options);
    }

    public async Task DisposeAsync()
    {
        if (DockerProbe.IsAvailable) await _postgres.DisposeAsync();
    }
}
