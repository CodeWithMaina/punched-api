using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.Attendance;
using PunchedApi.Application.Attendance.Verification;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Tests;

/// <summary>
/// Phase 2 location + QR credential lifecycle tests (plan §8.4, §18 acceptance):
/// mint → previous Active revoked + new Active returned once; rotate →
/// QR_REGENERATED and the old sheet scans as QR_REVOKED; revoke → location
/// unusable; delete with history ⇒ LOCATION_HAS_HISTORY. Cross-organisation ids
/// answer NOT_FOUND and never mutate anything (§10.2).
/// </summary>
public class AttendanceLocationServiceTests
{
    private sealed class Env
    {
        public ApplicationDbContext Context = null!;
        public AttendanceLocationService Service = null!;
        public Business Business = null!;
        public User Owner = null!;
        public User Staff = null!;
    }

    private static async Task<Env> CreateEnvAsync(SqliteConnection connection)
    {
        var context = BookingTestBase.CreateContext(connection);
        var owner = BookingTestBase.CreateOwner("owner@loc.test");
        var business = BookingTestBase.CreateBusiness(owner.Id, "Loc Biz");
        var staff = BookingTestBase.CreateStaff(business.Id, "staff@loc.test");

        await BookingTestBase.SeedAsync(context, owner, business, staff);
        context.ChangeTracker.Clear();

        return new Env
        {
            Context = context,
            Service = new AttendanceLocationService(context, TestHelpers.CreateLogger<AttendanceLocationService>()),
            Business = business,
            Owner = owner,
            Staff = staff,
        };
    }

    private static async Task<AttendanceLocationDetailResponse> CreateLocationAsync(Env env, string name)
    {
        var result = await env.Service.CreateLocationAsync(env.Owner.Id, new CreateAttendanceLocationRequest { Name = name });
        Assert.True(result.Success, result.Error?.Message);
        env.Context.ChangeTracker.Clear();
        return result.Data!;
    }

    private static AttendanceVerificationEngine VerifierEngine(ApplicationDbContext context) =>
        new(
            new IAttendanceVerifier[] { new AuthenticatedUserVerifier(context), new QrVerifier(context) },
            TestHelpers.CreateLogger<AttendanceVerificationEngine>());

    private static AttendanceVerificationContext ScanContext(Env env, string token) => new(
        env.Staff.Id,
        "Staff",
        env.Business.Id,
        env.Staff.StaffBusinessId,
        AttendanceEventType.ClockIn,
        AttendancePolicyService.CreateImplicitDefault(env.Business.Id),
        token,
        null,
        DateTime.UtcNow);

    // ── Locations CRUD ────────────────────────────────────────

    [Fact]
    public async Task CreateLocation_TrimsFields_AndRejectsDuplicateNamesCaseInsensitively()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        var created = await env.Service.CreateLocationAsync(env.Owner.Id, new CreateAttendanceLocationRequest
        {
            Name = "  Reception  ",
            Description = "  Front desk  ",
        });

        Assert.True(created.Success, created.Error?.Message);
        Assert.Equal("Reception", created.Data!.Name);
        Assert.Equal("Front desk", created.Data.Description);
        Assert.Equal(env.Business.Id, created.Data.BusinessId);
        Assert.True(created.Data.IsActive);
        Assert.False(created.Data.HasActiveCredential);

        var duplicate = await env.Service.CreateLocationAsync(env.Owner.Id, new CreateAttendanceLocationRequest { Name = "reception" });
        Assert.False(duplicate.Success);
        Assert.Equal("LOCATION_NAME_EXISTS", duplicate.Error!.Code);
    }

    [Fact]
    public async Task CreateLocation_RejectsBlankName_AndUnknownOwner()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        var blank = await env.Service.CreateLocationAsync(env.Owner.Id, new CreateAttendanceLocationRequest { Name = "   " });
        Assert.False(blank.Success);
        Assert.Equal("LOCATION_NAME_EXISTS", blank.Error!.Code);

        var customer = BookingTestBase.CreateCustomer("customer@loc.test");
        await BookingTestBase.SeedAsync(context, customer);
        context.ChangeTracker.Clear();

        var unknownOwner = await env.Service.CreateLocationAsync(customer.Id, new CreateAttendanceLocationRequest { Name = "Reception" });
        Assert.False(unknownOwner.Success);
        Assert.Equal("NOT_FOUND", unknownOwner.Error!.Code);
    }

    [Fact]
    public async Task UpdateLocation_AppliesProvidedFieldsOnly_AndKeepsNamesUnique()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        var reception = await CreateLocationAsync(env, "Reception");
        var warehouse = await CreateLocationAsync(env, "Warehouse");

        var updated = await env.Service.UpdateLocationAsync(env.Owner.Id, reception.Id,
            new UpdateAttendanceLocationRequest { Description = "New desc" });

        Assert.True(updated.Success, updated.Error?.Message);
        Assert.Equal("Reception", updated.Data!.Name);        // untouched
        Assert.Equal("New desc", updated.Data.Description);
        Assert.True(updated.Data.IsActive);

        // Renaming to its own name (any casing) is not a conflict.
        var self = await env.Service.UpdateLocationAsync(env.Owner.Id, reception.Id,
            new UpdateAttendanceLocationRequest { Name = "RECEPTION" });
        Assert.True(self.Success, self.Error?.Message);

        var clash = await env.Service.UpdateLocationAsync(env.Owner.Id, warehouse.Id,
            new UpdateAttendanceLocationRequest { Name = "reception" });
        Assert.False(clash.Success);
        Assert.Equal("LOCATION_NAME_EXISTS", clash.Error!.Code);
    }

    [Fact]
    public async Task UpdateLocation_Deactivating_IsAudited_AndListedOnlyWhenRequested()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        var reception = await CreateLocationAsync(env, "Reception");
        var warehouse = await CreateLocationAsync(env, "Warehouse");

        var deactivated = await env.Service.UpdateLocationAsync(env.Owner.Id, warehouse.Id,
            new UpdateAttendanceLocationRequest { IsActive = false });
        Assert.True(deactivated.Success, deactivated.Error?.Message);
        Assert.False(deactivated.Data!.IsActive);

        context.ChangeTracker.Clear();
        var logs = await context.ApiEventLogs.ToListAsync();
        Assert.Contains(logs, l => l.DetailsJson != null && l.DetailsJson.Contains("ATTENDANCE_LOCATION_DEACTIVATED"));

        var activeOnly = await env.Service.ListLocationsAsync(env.Owner.Id);
        Assert.True(activeOnly.Success, activeOnly.Error?.Message);
        Assert.Equal(reception.Id, Assert.Single(activeOnly.Data!).Id);

        var all = await env.Service.ListLocationsAsync(env.Owner.Id, includeInactive: true);
        Assert.Equal(2, all.Data!.Count);
        Assert.Equal("Reception", all.Data![0].Name); // ordered by name
    }

    [Fact]
    public async Task DeleteLocation_WithScanHistory_IsRefusedWithLocationHasHistory()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        var location = await CreateLocationAsync(env, "Reception");

        await BookingTestBase.SeedAsync(context, new AttendanceEvent
        {
            Id = Guid.NewGuid(),
            BusinessId = env.Business.Id,
            StaffUserId = env.Staff.Id,
            EventType = AttendanceEventType.ClockIn,
            OccurredAt = DateTime.UtcNow,
            AttendanceLocationId = location.Id,
            CreatedByUserId = env.Staff.Id,
            RecordedAt = DateTime.UtcNow,
            VerificationSummaryJson = "[]",
        });
        context.ChangeTracker.Clear();

        var refused = await env.Service.DeleteLocationAsync(env.Owner.Id, location.Id);

        Assert.False(refused.Success);
        Assert.Equal("LOCATION_HAS_HISTORY", refused.Error!.Code);

        // Nothing was removed.
        context.ChangeTracker.Clear();
        Assert.NotNull(await context.AttendanceLocations.FindAsync(location.Id));
    }

    [Fact]
    public async Task DeleteLocation_WithoutHistory_RemovesIt_AndIsAudited()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        var location = await CreateLocationAsync(env, "Reception");

        var deleted = await env.Service.DeleteLocationAsync(env.Owner.Id, location.Id);

        Assert.True(deleted.Success, deleted.Error?.Message);
        Assert.True(deleted.Data);

        context.ChangeTracker.Clear();
        Assert.Null(await context.AttendanceLocations.FindAsync(location.Id));

        var logs = await context.ApiEventLogs.ToListAsync();
        Assert.Contains(logs, l => l.DetailsJson != null && l.DetailsJson.Contains("ATTENDANCE_LOCATION_DELETED"));
    }

    // ── QR credential lifecycle ───────────────────────────────

    [Fact]
    public async Task MintQr_ReturnsRawTokenOnce_AndStoresOnlyItsHash()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        var location = await CreateLocationAsync(env, "Reception");

        var minted = await env.Service.MintQrAsync(env.Owner.Id, location.Id);

        Assert.True(minted.Success, minted.Error?.Message);
        Assert.StartsWith(AttendanceTokenFactory.PayloadPrefix, minted.Data!.Token);
        Assert.Equal("ACTIVE", minted.Data.Status);
        Assert.Equal(location.Id, minted.Data.LocationId);

        context.ChangeTracker.Clear();
        var stored = Assert.Single(await context.AttendanceQrCredentials
            .Where(c => c.AttendanceLocationId == location.Id).ToListAsync());

        Assert.Equal(minted.Data.CredentialId, stored.Id);
        Assert.Equal(AttendanceTokenFactory.HashToken(minted.Data.Token), stored.TokenHash);
        Assert.DoesNotContain(minted.Data.Token, stored.TokenHash);
        Assert.Equal(AttendanceCredentialStatus.Active, stored.Status);
        Assert.Equal(env.Owner.Id, stored.CreatedByUserId);

        // The location now reports a live credential, still without its token.
        var listed = await env.Service.ListLocationsAsync(env.Owner.Id);
        Assert.True(listed.Data![0].HasActiveCredential);
    }

    [Fact]
    public async Task MintQr_RevokesPreviousCredential_AndOldSheetScansAsQrRevoked()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        var location = await CreateLocationAsync(env, "Reception");

        var first = await env.Service.MintQrAsync(env.Owner.Id, location.Id);
        var second = await env.Service.MintQrAsync(env.Owner.Id, location.Id);
        Assert.True(first.Success && second.Success);

        context.ChangeTracker.Clear();
        var credentials = await context.AttendanceQrCredentials
            .Where(c => c.AttendanceLocationId == location.Id).ToListAsync();

        // Exactly one live credential per location at every instant (§8.4).
        Assert.Equal(2, credentials.Count);
        Assert.Equal(1, credentials.Count(c => c.Status == AttendanceCredentialStatus.Active));

        var superseded = credentials.Single(c => c.TokenHash == AttendanceTokenFactory.HashToken(first.Data!.Token));
        Assert.Equal(AttendanceCredentialStatus.Revoked, superseded.Status);
        Assert.NotNull(superseded.RevokedAt);
        Assert.Equal(env.Owner.Id, superseded.RevokedByUserId);

        // The superseded hash is RETAINED so an old sheet is actionable...
        var staleScan = await VerifierEngine(context).VerifyAsync(ScanContext(env, first.Data!.Token));
        Assert.False(staleScan.Success);
        Assert.Equal("QR_REVOKED", staleScan.Error!.Code);

        // ...and the new one works.
        var freshScan = await VerifierEngine(context).VerifyAsync(ScanContext(env, second.Data!.Token));
        Assert.True(freshScan.Success, freshScan.Error?.Message);
        Assert.Equal(second.Data.CredentialId, freshScan.Data!.Credential!.Id);
    }

    [Fact]
    public async Task RotateQr_IsAuditedAsQrRegenerated_AndRevokeLeavesNoActiveCredential()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        var location = await CreateLocationAsync(env, "Reception");

        var rotated = await env.Service.RotateQrAsync(env.Owner.Id, location.Id);
        Assert.True(rotated.Success, rotated.Error?.Message);

        context.ChangeTracker.Clear();
        var logs = await context.ApiEventLogs.ToListAsync();
        Assert.Contains(logs, l => l.DetailsJson != null && l.DetailsJson.Contains("QR_REGENERATED"));

        var revoked = await env.Service.RevokeQrAsync(env.Owner.Id, location.Id);
        Assert.True(revoked.Success, revoked.Error?.Message);

        context.ChangeTracker.Clear();
        Assert.DoesNotContain(await context.AttendanceQrCredentials.ToListAsync(),
            c => c.Status == AttendanceCredentialStatus.Active);

        // The location is now unclockable, and revoking twice is a NOT_FOUND.
        var again = await env.Service.RevokeQrAsync(env.Owner.Id, location.Id);
        Assert.False(again.Success);
        Assert.Equal("NOT_FOUND", again.Error!.Code);

        var scan = await VerifierEngine(context).VerifyAsync(ScanContext(env, rotated.Data!.Token));
        Assert.False(scan.Success);
        Assert.Equal("QR_REVOKED", scan.Error!.Code);
    }

    [Fact]
    public async Task MintQr_OnInactiveLocation_IsRefused()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        var location = await CreateLocationAsync(env, "Reception");
        await env.Service.UpdateLocationAsync(env.Owner.Id, location.Id, new UpdateAttendanceLocationRequest { IsActive = false });
        context.ChangeTracker.Clear();

        var result = await env.Service.MintQrAsync(env.Owner.Id, location.Id);

        Assert.False(result.Success);
        Assert.Equal("LOCATION_INACTIVE", result.Error!.Code);
    }

    [Fact]
    public async Task QrMutations_ForAnotherOrganisation_ReturnNotFound_AndChangeNothing()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        // Another organisation, fully set up, with its own legitimate credential.
        var otherOwner = BookingTestBase.CreateOwner("other-owner@loc.test");
        var otherBusiness = BookingTestBase.CreateBusiness(otherOwner.Id, "Other Biz");
        var otherLocation = new AttendanceLocation
        {
            Id = Guid.NewGuid(),
            BusinessId = otherBusiness.Id,
            Name = "Other Entrance",
            IsActive = true,
            CreatedByUserId = otherOwner.Id,
            CreatedAt = DateTime.UtcNow,
        };
        await BookingTestBase.SeedAsync(context, otherOwner, otherBusiness, otherLocation);
        context.ChangeTracker.Clear();

        var foreignMint = await env.Service.MintQrAsync(otherOwner.Id, otherLocation.Id);
        Assert.True(foreignMint.Success, foreignMint.Error?.Message);
        context.ChangeTracker.Clear();

        // env.Owner must not be able to read or touch any of it.
        var get = await env.Service.GetLocationAsync(env.Owner.Id, otherLocation.Id);
        var update = await env.Service.UpdateLocationAsync(env.Owner.Id, otherLocation.Id,
            new UpdateAttendanceLocationRequest { Name = "Hijacked" });
        var delete = await env.Service.DeleteLocationAsync(env.Owner.Id, otherLocation.Id);
        var mint = await env.Service.MintQrAsync(env.Owner.Id, otherLocation.Id);
        var rotate = await env.Service.RotateQrAsync(env.Owner.Id, otherLocation.Id);
        var revoke = await env.Service.RevokeQrAsync(env.Owner.Id, otherLocation.Id);

        foreach (var succeeded in new[] { get.Success, update.Success, delete.Success, mint.Success, rotate.Success, revoke.Success })
            Assert.False(succeeded);

        // NOT_FOUND (never FORBIDDEN) — the non-enumerable answer (§10.2).
        Assert.Equal("NOT_FOUND", get.Error!.Code);
        Assert.Equal("NOT_FOUND", update.Error!.Code);
        Assert.Equal("NOT_FOUND", delete.Error!.Code);
        Assert.Equal("NOT_FOUND", mint.Error!.Code);
        Assert.Equal("NOT_FOUND", rotate.Error!.Code);
        Assert.Equal("NOT_FOUND", revoke.Error!.Code);

        // Untouched: still one Active credential, still its original name.
        context.ChangeTracker.Clear();
        var untouched = Assert.Single(await context.AttendanceQrCredentials
            .Where(c => c.AttendanceLocationId == otherLocation.Id).ToListAsync());
        Assert.Equal(AttendanceCredentialStatus.Active, untouched.Status);
        var location = await context.AttendanceLocations.FindAsync(otherLocation.Id);
        Assert.Equal("Other Entrance", location!.Name);
    }

    [Fact]
    public async Task AuditRows_CarryActionAndCredentialId_ButNeverTheRawTokenOrHash()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        var location = await CreateLocationAsync(env, "Reception");
        var minted = await env.Service.MintQrAsync(env.Owner.Id, location.Id);
        Assert.True(minted.Success, minted.Error?.Message);

        context.ChangeTracker.Clear();
        var auditJson = string.Join("|", (await context.ApiEventLogs.ToListAsync()).Select(l => l.DetailsJson));

        Assert.Contains("QR_CREATED", auditJson);
        Assert.Contains(minted.Data!.CredentialId.ToString(), auditJson);
        Assert.Contains(location.Id.ToString(), auditJson);

        // §8.3 / §19.3 — the token and its hash never reach an audit row.
        Assert.DoesNotContain(minted.Data.Token, auditJson);
        Assert.DoesNotContain(AttendanceTokenFactory.HashToken(minted.Data.Token), auditJson);
    }

    // ── Token factory (§8.3) ──────────────────────────────────

    [Fact]
    public void TokenFactory_ProducesNamespacedHighEntropyPayload_WithStableSha256Hex()
    {
        var payload = AttendanceTokenFactory.CreatePayload();

        Assert.StartsWith("punched:attendance:v1:", payload);
        Assert.True(AttendanceTokenFactory.HasValidPrefix(payload));
        Assert.False(AttendanceTokenFactory.HasValidPrefix("https://example.com/attendance?v=1"));

        // 32 CSPRNG bytes → base64url, padding trimmed → 43 chars.
        var token = payload[AttendanceTokenFactory.PayloadPrefix.Length..];
        Assert.Equal(43, token.Length);

        var hash = AttendanceTokenFactory.HashToken(payload);
        Assert.Equal(64, hash.Length);
        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.Equal(hash, AttendanceTokenFactory.HashToken(payload)); // deterministic
        Assert.NotEqual(payload, AttendanceTokenFactory.CreatePayload()); // CSPRNG, no repeats
    }
}