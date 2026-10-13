using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Tests;

/// <summary>
/// A real SQLite relational context for media processing tests. The pipeline depends on
/// relational behaviour (unique index on the source key, check constraints), so an
/// in-memory provider would not exercise the same code path.
/// </summary>
internal static class MediaProcessingTestDb
{
    public static ApplicationDbContext Create()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;
        var context = new ApplicationDbContext(options);
        context.Database.EnsureCreated();
        return context;
    }
}
