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
}
