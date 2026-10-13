using Microsoft.EntityFrameworkCore;
using PunchedApi.Domain.Entities;
using Testcontainers.PostgreSql;

namespace PunchedApi.Tests;

public sealed class MediaPostgresSchemaTests : IClassFixture<MediaPostgresFixture>
{
    private readonly MediaPostgresFixture _fixture;
    public MediaPostgresSchemaTests(MediaPostgresFixture fixture) => _fixture = fixture;

    [RequiresDockerFact]
    public async Task MediaSchema_EnforcesOwnershipAndRelationshipConstraints()
    {
        await using var db = _fixture.CreateContext();
        var ownerCheck = await db.Database.SqlQueryRaw<int>(@"SELECT count(*) AS ""Value"" FROM pg_constraint WHERE conname = 'ck_media_owner_xor'").SingleAsync();
        var sourceUnique = await db.Database.SqlQueryRaw<int>(@"SELECT count(*) AS ""Value"" FROM pg_indexes WHERE indexname = 'ux_media_source_key'").SingleAsync();
        var featuredUnique = await db.Database.SqlQueryRaw<int>(@"SELECT count(*) AS ""Value"" FROM pg_indexes WHERE indexname = 'ux_business_media_one_featured'").SingleAsync();
        var purgeIndex = await db.Database.SqlQueryRaw<int>(@"SELECT count(*) AS ""Value"" FROM pg_indexes WHERE indexname = 'ix_media_delivery_purge'").SingleAsync();
        Assert.Equal(1, ownerCheck); Assert.Equal(1, sourceUnique); Assert.Equal(1, featuredUnique); Assert.Equal(1, purgeIndex);

        await Assert.ThrowsAsync<DbUpdateException>(async () =>
        {
            db.Media.Add(new Domain.Entities.Media
            {
                Id = Guid.NewGuid(), BusinessId = Guid.NewGuid(), OwnerUserId = Guid.NewGuid(),
                UploadedByUserId = Guid.NewGuid(), Purpose = MediaPurposes.BusinessLogo,
                SourceKey = "pending/test/xor.bin"
            });
            await db.SaveChangesAsync();
        });
    }

    /// <summary>
    /// Migration 20261013104508_AddMediaLifecycleTimestamps must produce two nullable
    /// timestamptz columns on media. The fixture applies every EF migration via
    /// MigrateAsync(), so this also proves the migration is registrable and applies
    /// cleanly on a clean Postgres.
    /// </summary>
    [RequiresDockerFact]
    public async Task MediaLifecycleTimestampColumns_ExistAndAreNullable()
    {
        await using var db = _fixture.CreateContext();
        var columns = await db.Database.SqlQueryRaw<int>(@"
            SELECT count(*) AS ""Value""
            FROM information_schema.columns
            WHERE table_name = 'media'
              AND column_name IN ('uploaded_at', 'ready_at')
              AND data_type = 'timestamp with time zone'
              AND is_nullable = 'YES'").SingleAsync();
        Assert.Equal(2, columns);
    }
}

/// <summary>
/// Model-side verification of the lifecycle-timestamp migration. Deliberately separate
/// from <see cref="MediaPostgresSchemaTests"/> so it runs without the Postgres fixture
/// (and therefore without Docker): it asserts the EF model maps the anchors to the exact
/// snake_case columns migration 20261013104508 creates.
/// </summary>
public sealed class MediaLifecycleModelMappingTests
{
    [Fact]
    public void MediaModel_MapsLifecycleTimestampAnchors()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var entity = context.Model.FindEntityType(typeof(Media))!;

        var uploaded = entity.FindProperty(nameof(Media.UploadedAt))!;
        Assert.Equal("uploaded_at", uploaded.GetColumnName());
        Assert.True(uploaded.IsNullable);

        var ready = entity.FindProperty(nameof(Media.ReadyAt))!;
        Assert.Equal("ready_at", ready.GetColumnName());
        Assert.True(ready.IsNullable);
    }
}
