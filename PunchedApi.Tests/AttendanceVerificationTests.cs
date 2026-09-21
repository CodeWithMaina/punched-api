using Microsoft.Data.Sqlite;
using PunchedApi.Application.Attendance;
using PunchedApi.Application.Attendance.Verification;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Tests;

/// <summary>
/// Phase 2 verification matrix (plan §18.2 V1–V10) plus the engine's
/// ordering/short-circuit/fail-closed rules (§7.2, §7.4). SQLite in-memory
/// with real indexes (the BookingTestBase convention) and
/// TestHelpers.CreateLogger&lt;T&gt;().
/// The QR token is never asserted in cleartext state that leaves the server —
/// only its SHA-256 hash is ever stored.
/// </summary>
public class AttendanceVerificationTests
{
    private sealed class Env
    {
        public ApplicationDbContext Context = null!;
        public Business Business = null!;
        public User Owner = null!;
        public User Staff = null!;
        public User UnlinkedStaff = null!;
        public AttendanceLocation Location = null!;
        public AttendanceQrCredential Credential = null!;
        public string RawToken = string.Empty;
    }

    private static async Task<Env> CreateEnvAsync(SqliteConnection connection)
    {
        var context = BookingTestBase.CreateContext(connection);
        var owner = BookingTestBase.CreateOwner("owner@attendance.test");
        var business = BookingTestBase.CreateBusiness(owner.Id, "Attendance Biz");
        var staff = BookingTestBase.CreateStaff(business.Id, "staff@attendance.test");

        // Staff with no linked business (V9) — role must still be Staff.
        var unlinked = BookingTestBase.CreateStaff(business.Id, "unlinked@attendance.test");
        unlinked.StaffBusinessId = null;

        var location = new AttendanceLocation
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            Name = "Reception",
            IsActive = true,
            CreatedByUserId = owner.Id,
            CreatedAt = DateTime.UtcNow,
        };

        var rawToken = AttendanceTokenFactory.CreatePayload();
        var credential = new AttendanceQrCredential
        {
            Id = Guid.NewGuid(),
            BusinessId = business.Id,
            AttendanceLocationId = location.Id,
            TokenHash = AttendanceTokenFactory.HashToken(rawToken),
            Status = AttendanceCredentialStatus.Active,
            CreatedByUserId = owner.Id,
            CreatedAt = DateTime.UtcNow,
        };

        await BookingTestBase.SeedAsync(context, owner, business, staff, unlinked, location, credential);

        return new Env
        {
            Context = context,
            Business = business,
            Owner = owner,
            Staff = staff,
            UnlinkedStaff = unlinked,
            Location = location,
            Credential = credential,
            RawToken = rawToken,
        };
    }

    private static AttendanceVerificationEngine CreateEngine(ApplicationDbContext context) =>
        new(
            new IAttendanceVerifier[] { new AuthenticatedUserVerifier(context), new QrVerifier(context) },
            TestHelpers.CreateLogger<AttendanceVerificationEngine>());

    private static AttendancePolicy Policy(string json = AttendancePolicyService.DefaultRequiredVerificationsJson) => new()
    {
        Id = Guid.NewGuid(),
        BusinessId = Guid.Empty,
        Mode = AttendanceMode.Standard,
        RequiredVerificationsJson = json,
        IsActive = true,
    };

    private static AttendanceVerificationContext Ctx(
        Env env,
        Guid actorUserId,
        string actorRole,
        Guid? staffBusinessId,
        AttendancePolicy? policy = null,
        string? token = null) => new(
            actorUserId,
            actorRole,
            env.Business.Id,
            staffBusinessId,
            AttendanceEventType.ClockIn,
            policy ?? Policy(),
            token,
            null,
            DateTime.UtcNow);

    private static AttendanceVerificationContext StaffCtx(Env env, AttendancePolicy? policy = null, string? token = null) =>
        Ctx(env, env.Staff.Id, "Staff", env.Staff.StaffBusinessId, policy, token);

    private static AttendanceVerificationContext OwnerCtx(Env env, AttendancePolicy? policy = null, string? token = null) =>
        Ctx(env, env.Owner.Id, "Business", null, policy, token);

    // ── V1 / V2 — happy paths ─────────────────────────────────

    [Fact]
    public async Task V1_LinkedStaffWithValidQr_Passes_AndReturnsCredentialAndLocation()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        var result = await CreateEngine(context).VerifyAsync(StaffCtx(env, token: env.RawToken));

        Assert.True(result.Success, result.Error?.Message);
        Assert.True(result.Data!.Passed);
        Assert.Null(result.Data.FailureCode);
        Assert.Equal(env.Credential.Id, result.Data.Credential!.Id);
        Assert.Equal(env.Location.Id, result.Data.Location!.Id);
        Assert.Equal(2, result.Data.Summaries.Count); // AUTHENTICATED_USER + QR
    }

    [Fact]
    public async Task V2_OwnerActor_IsAlsoValid_AndTokenlessPolicyStillWorks()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        var owner = await CreateEngine(context).VerifyAsync(OwnerCtx(env, token: env.RawToken));
        Assert.True(owner.Success, owner.Error?.Message);
        Assert.True(owner.Data!.Passed);

        // V8 — the engine is genuinely policy-driven, not QR-hard-coded.
        var tokenless = await CreateEngine(context).VerifyAsync(
            OwnerCtx(env, Policy("[\"AUTHENTICATED_USER\"]")));
        Assert.True(tokenless.Success, tokenless.Error?.Message);
        Assert.True(tokenless.Data!.Passed);
        Assert.Single(tokenless.Data.Summaries);
        Assert.Null(tokenless.Data.Credential);
    }

    // ── V3 — no probing oracle ────────────────────────────────

    [Fact]
    public async Task V3_UnknownAndMalformedTokens_AreIndistinguishable()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;
        var engine = CreateEngine(context);

        var unknown = await engine.VerifyAsync(StaffCtx(env, token: "punched:attendance:v1:definitelyNotARealToken"));
        var malformed = await engine.VerifyAsync(StaffCtx(env, token: "hello-world"));
        var missing = await engine.VerifyAsync(StaffCtx(env, token: null));

        foreach (var result in new[] { unknown, malformed, missing })
        {
            Assert.False(result.Success);
            Assert.Equal("INVALID_QR", result.Error!.Code);
        }

        // Same code AND same message — no probing oracle.
        Assert.Equal(unknown.Error!.Message, malformed.Error!.Message);
        Assert.Equal(unknown.Error!.Message, missing.Error!.Message);
    }

    // ── V4–V6 — QR failure codes ──────────────────────────────

    [Fact]
    public async Task V4_RevokedCredential_ReturnsQrRevoked_NotInvalidQr()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        var credential = await context.AttendanceQrCredentials.FindAsync(env.Credential.Id);
        credential!.Status = AttendanceCredentialStatus.Revoked;
        credential.RevokedAt = DateTime.UtcNow;
        credential.RevokedByUserId = env.Owner.Id;
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var result = await CreateEngine(context).VerifyAsync(StaffCtx(env, token: env.RawToken));

        Assert.False(result.Success);
        Assert.Equal("QR_REVOKED", result.Error!.Code);
    }

    [Fact]
    public async Task V5_ForeignBusinessCredential_ReturnsQrWrongOrganization()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        // A second, fully separate organisation with its own location + QR.
        var otherOwner = BookingTestBase.CreateOwner("other-owner@attendance.test");
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
        var otherToken = AttendanceTokenFactory.CreatePayload();
        var otherCredential = new AttendanceQrCredential
        {
            Id = Guid.NewGuid(),
            BusinessId = otherBusiness.Id,
            AttendanceLocationId = otherLocation.Id,
            TokenHash = AttendanceTokenFactory.HashToken(otherToken),
            Status = AttendanceCredentialStatus.Active,
            CreatedByUserId = otherOwner.Id,
            CreatedAt = DateTime.UtcNow,
        };
        await BookingTestBase.SeedAsync(context, otherOwner, otherBusiness, otherLocation, otherCredential);
        context.ChangeTracker.Clear();

        var result = await CreateEngine(context).VerifyAsync(StaffCtx(env, token: otherToken));

        Assert.False(result.Success);
        Assert.Equal("QR_WRONG_ORGANIZATION", result.Error!.Code);
    }

    [Fact]
    public async Task V6_ActiveCredentialOnInactiveLocation_ReturnsLocationInactive()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        var location = await context.AttendanceLocations.FindAsync(env.Location.Id);
        location!.IsActive = false;
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var result = await CreateEngine(context).VerifyAsync(StaffCtx(env, token: env.RawToken));

        Assert.False(result.Success);
        Assert.Equal("LOCATION_INACTIVE", result.Error!.Code);
    }

    // ── V7 / V9 / V10 — fail-closed and actor checks ──────────

    [Fact]
    public async Task V7_RequiredButUnregisteredMethod_FailsClosed()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        // GPS exists in the enum but has no verifier yet — must fail closed,
        // never silently skip (§7.2).
        var result = await CreateEngine(context).VerifyAsync(
            StaffCtx(env, Policy("[\"AUTHENTICATED_USER\",\"GPS\"]"), token: env.RawToken));

        Assert.False(result.Success);
        Assert.Equal("VERIFICATION_METHOD_UNAVAILABLE", result.Error!.Code);
        Assert.Contains("GPS", result.Error!.Message);
    }

    [Fact]
    public async Task Engine_FailsClosed_OnEmptyOrMalformedPolicyJson()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;
        var engine = CreateEngine(context);

        var empty = await engine.VerifyAsync(StaffCtx(env, Policy("[]"), token: env.RawToken));
        Assert.False(empty.Success);
        Assert.Equal("VERIFICATION_METHOD_UNAVAILABLE", empty.Error!.Code);

        var malformed = await engine.VerifyAsync(StaffCtx(env, Policy("not-json"), token: env.RawToken));
        Assert.False(malformed.Success);
        Assert.Equal("VERIFICATION_METHOD_UNAVAILABLE", malformed.Error!.Code);
    }

    [Fact]
    public async Task V9_StaffWithoutLinkedBusiness_ReturnsNotLinked()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        var result = await CreateEngine(context).VerifyAsync(
            Ctx(env, env.UnlinkedStaff.Id, "Staff", staffBusinessId: null, token: env.RawToken));

        Assert.False(result.Success);
        Assert.Equal("NOT_LINKED", result.Error!.Code);
    }

    [Fact]
    public async Task V10_ActorWithoutAttendanceClockPermission_ReturnsForbidden()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        var customer = BookingTestBase.CreateCustomer("customer@attendance.test");
        await BookingTestBase.SeedAsync(context, customer);
        context.ChangeTracker.Clear();

        var result = await CreateEngine(context).VerifyAsync(
            Ctx(env, customer.Id, "Customer", staffBusinessId: null, token: env.RawToken));

        Assert.False(result.Success);
        Assert.Equal("FORBIDDEN", result.Error!.Code);
    }

    [Fact]
    public async Task Engine_RunsAuthenticatedUserFirst_AndShortCircuits()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        // Unlinked actor AND a bogus token: AUTHENTICATED_USER runs first, so
        // the verdict is NOT_LINKED — proving ordering and that QR never ran.
        var result = await CreateEngine(context).VerifyAsync(
            Ctx(env, env.UnlinkedStaff.Id, "Staff", staffBusinessId: null, token: "nonsense"));

        Assert.False(result.Success);
        Assert.Equal("NOT_LINKED", result.Error!.Code);
    }

    [Fact]
    public async Task Summaries_CarryTypeAndPassedFlags_ButNeverTheRawToken()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        var result = await CreateEngine(context).VerifyAsync(StaffCtx(env, token: env.RawToken));

        Assert.True(result.Success, result.Error?.Message);
        var joined = string.Join("|", result.Data!.Summaries);
        Assert.Contains("AUTHENTICATED_USER", joined);
        Assert.Contains("QR", joined);
        Assert.Contains("\"passed\":true", joined);
        Assert.DoesNotContain(env.RawToken, joined);
        Assert.DoesNotContain(AttendanceTokenFactory.HashToken(env.RawToken), joined);
    }

    // ── Wire-value contract (§6.6) ────────────────────────────

    [Fact]
    public void WireValues_AreScreamingSnake_AndRoundTrip()
    {
        Assert.Equal("AUTHENTICATED_USER", AttendanceVerification.WireValue(AttendanceVerificationMethod.AuthenticatedUser));
        Assert.Equal("QR", AttendanceVerification.WireValue(AttendanceVerificationMethod.Qr));
        Assert.Equal("TRUSTED_DEVICE", AttendanceVerification.WireValue(AttendanceVerificationMethod.TrustedDevice));
        Assert.Equal("STANDARD", AttendanceVerification.WireValue(AttendanceMode.Standard));

        Assert.Equal(AttendanceVerificationMethod.Qr, AttendanceVerification.ParseWireValue("qr"));
        Assert.Equal(AttendanceMode.Standard, AttendanceVerification.ParseModeWireValue("standard"));
        Assert.Null(AttendanceVerification.ParseWireValue("NOT_A_METHOD"));
        Assert.Null(AttendanceVerification.ParseModeWireValue(null));
    }

    [Fact]
    public void ParseRequiredMethods_SkipsUnknownValues_AndDeduplicates()
    {
        var methods = AttendanceVerification.ParseRequiredMethods("[\"QR\",\"NOT_A_METHOD\",\"QR\",\"AUTHENTICATED_USER\"]");

        Assert.Equal(new[] { AttendanceVerificationMethod.Qr, AttendanceVerificationMethod.AuthenticatedUser }, methods);
        Assert.Empty(AttendanceVerification.ParseRequiredMethods(null));
    }
}