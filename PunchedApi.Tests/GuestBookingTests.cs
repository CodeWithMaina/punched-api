using System.IdentityModel.Tokens.Jwt;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;

namespace PunchedApi.Tests;

/// <summary>
/// Guest booking.
///
/// <para>These are the security properties of the flow, not its plumbing: an
/// identity is verified before any token exists, a code cannot be guessed,
/// replayed, or used after it expires, an existing account cannot be taken over,
/// a verified guest gets an ordinary scoped customer token, and nothing works
/// across a tenant boundary or without the booking module.</para>
/// </summary>
public sealed class GuestBookingTests
{
    private const string JwtSecret = "guest-booking-tests-secret-key-that-is-long-enough-1234567890";

    /// <summary>The business this test's fixture seeded (ids are generated per run).</summary>
    private Guid _businessId;

    private sealed class RecordingEmailService : IEmailService
    {
        public List<(string Email, string Code)> Codes { get; } = new();

        public Task<bool> SendVerificationCodeAsync(string email, string code)
        {
            Codes.Add((email, code));
            return Task.FromResult(true);
        }

        public Task<bool> SendPasswordResetCodeAsync(string email, string code) => Task.FromResult(true);
        public Task<bool> SendWelcomeAsync(string email, string name) => Task.FromResult(true);
        public Task<bool> SendStampNotificationAsync(string email, string businessName, int stampNumber, int stampsRequired) => Task.FromResult(true);
        public Task<bool> SendRewardReadyAsync(string email, string businessName, string rewardDescription) => Task.FromResult(true);
        public Task<bool> SendStaffInvitationAsync(string email, string businessName, string invitationUrl, DateTime expiresAt) => Task.FromResult(true);
    }

    private static JwtSettings JwtSettings() => new()
    {
        Secret = JwtSecret,
        Issuer = "PunchedTests",
        Audience = "PunchedTests",
        AccessTokenExpiryMinutes = 60
    };

    private static GuestBookingService Service(
        Infrastructure.Data.ApplicationDbContext context,
        IEmailService email,
        StubModuleEntitlements? modules = null,
        ITenantContext? tenant = null) =>
        new(context, email, modules ?? new StubModuleEntitlements("appointments"),
            new JwtTokenService(Options.Create(JwtSettings())),
            Options.Create(JwtSettings()),
            TestHelpers.CreateLogger<GuestBookingService>(),
            tenant: tenant);

    private static StartGuestBookingRequest Start(
        string phone = "0712 345 678", string email = "guest@test.com") =>
        new() { FullName = "Peter Chege", PhoneNumber = phone, Email = email };

    private static VerifyGuestBookingRequest Verify(string code) =>
        new() { PhoneNumber = "0712 345 678", Code = code };

    /// <summary>Seeds one owner + business and records the generated business id.</summary>
    private async Task<Infrastructure.Data.ApplicationDbContext> SeededAsync(SqliteConnection connection)
    {
        var context = BookingTestBase.CreateContext(connection);
        var owner = BookingTestBase.CreateOwner();
        var business = BookingTestBase.CreateBusiness(owner.Id);
        await BookingTestBase.SeedAsync(context, owner, business);
        _businessId = business.Id;
        return context;
    }

    [Fact]
    public async Task Start_creates_an_unverified_customer_attached_to_the_business()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = await SeededAsync(connection);
        var email = new RecordingEmailService();

        var result = await Service(context, email).StartAsync(_businessId, Start());

        Assert.True(result.Success, result.Error?.Message);
        // The full address is never echoed back.
        Assert.Equal("g•••@test.com", result.Data!.Email);
        Assert.Equal("254712345678", result.Data.PhoneNumber);

        var user = await context.Users.AsNoTracking().Include(u => u.Auth)
            .SingleAsync(u => u.PhoneNumber == "254712345678");
        Assert.Equal("Peter Chege", user.FullName);
        Assert.Equal(UserRole.Customer, user.Role);
        // Unverified until the code is presented, and the password is not the
        // visitor's to know.
        Assert.False(user.Auth!.IsVerified);
        Assert.False(string.IsNullOrWhiteSpace(user.Auth.PasswordHash));

        var membership = await context.BusinessMemberships.AsNoTracking()
            .SingleAsync(m => m.UserId == user.Id);
        Assert.Equal(BusinessMembershipRole.Customer, membership.Role);
        Assert.Equal(BusinessMembershipStatus.Active, membership.Status);

        // Exactly one code, sent to the address the visitor gave.
        var sent = Assert.Single(email.Codes);
        Assert.Equal("guest@test.com", sent.Email);
        Assert.Equal(6, sent.Code.Length);
    }

    [Fact]
    public async Task Start_refuses_an_address_that_already_has_a_verified_account()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = await SeededAsync(connection);
        var email = new RecordingEmailService();

        // An existing, verified customer of the platform.
        await BookingTestBase.SeedAsync(context, BookingTestBase.CreateCustomer("guest@test.com"));

        var result = await Service(context, email).StartAsync(_businessId, Start());

        Assert.Equal("ACCOUNT_EXISTS", result.Error?.Code);
        // Nothing was emailed: this must not become an account-takeover path.
        Assert.Empty(email.Codes);
    }

    [Fact]
    public async Task Start_enforces_a_resend_cooldown()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = await SeededAsync(connection);
        var email = new RecordingEmailService();
        var service = Service(context, email);

        Assert.True((await service.StartAsync(_businessId, Start())).Success);
        var second = await service.StartAsync(_businessId, Start());

        Assert.Equal("RESEND_TOO_SOON", second.Error?.Code);
        Assert.Single(email.Codes);
    }

    [Fact]
    public async Task A_verified_identity_cannot_be_duplicated_by_a_second_guest_booking()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = await SeededAsync(connection);
        var email = new RecordingEmailService();
        var service = Service(context, email);

        await service.StartAsync(_businessId, Start());
        Assert.True((await service.VerifyAsync(_businessId, Verify(email.Codes[0].Code))).Success);
        Assert.Equal(1, await context.Users.CountAsync(u => u.PhoneNumber == "254712345678"));

        // The same address is now a real account: a second guest booking must not
        // create a second customer for it.
        Assert.Equal("ACCOUNT_EXISTS", (await service.StartAsync(_businessId, Start())).Error?.Code);

        // And neither must the same number under a different address.
        var otherAddress = await service.StartAsync(_businessId, Start(email: "other@test.com"));
        Assert.Equal("ACCOUNT_EXISTS", otherAddress.Error?.Code);

        Assert.Equal(1, await context.Users.CountAsync(u => u.PhoneNumber == "254712345678"));
    }

    [Fact]
    public async Task Verify_rejects_a_wrong_code_and_counts_the_attempt()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = await SeededAsync(connection);
        var email = new RecordingEmailService();
        var service = Service(context, email);

        await service.StartAsync(_businessId, Start());
        var result = await service.VerifyAsync(_businessId, Verify("000000"));

        Assert.Equal("CODE_INVALID", result.Error?.Code);
        Assert.Null(result.Data);

        var auth = await context.UserAuths.AsNoTracking()
            .SingleAsync(a => a.Email == "guest@test.com");
        Assert.Equal(1, auth.VerificationCodeAttempts);
        Assert.False(auth.IsVerified);
    }

    [Fact]
    public async Task Verify_locks_out_after_the_attempt_ceiling()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = await SeededAsync(connection);
        var email = new RecordingEmailService();
        var service = Service(context, email);

        await service.StartAsync(_businessId, Start());

        for (var attempt = 0; attempt < 5; attempt++)
            await service.VerifyAsync(_businessId, Verify("000000"));

        // Even the correct code is refused once the ceiling is reached.
        var result = await service.VerifyAsync(_businessId, Verify(email.Codes[0].Code));

        Assert.Equal("TOO_MANY_ATTEMPTS", result.Error?.Code);
    }

    [Fact]
    public async Task Verify_rejects_an_expired_code()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = await SeededAsync(connection);
        var email = new RecordingEmailService();
        var service = Service(context, email);

        await service.StartAsync(_businessId, Start());
        var auth = await context.UserAuths.SingleAsync(a => a.Email == "guest@test.com");
        auth.VerificationCodeExpiresAt = DateTime.UtcNow.AddSeconds(-1);
        await context.SaveChangesAsync();

        var result = await service.VerifyAsync(_businessId, Verify(email.Codes[0].Code));

        Assert.Equal("CODE_EXPIRED", result.Error?.Code);
    }

    [Fact]
    public async Task Verify_issues_a_customer_token_scoped_to_the_business_and_is_single_use()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = await SeededAsync(connection);
        var email = new RecordingEmailService();
        var service = Service(context, email);

        await service.StartAsync(_businessId, Start());
        var code = email.Codes[0].Code;

        var verified = await service.VerifyAsync(_businessId, Verify(code));

        Assert.True(verified.Success, verified.Error?.Message);
        Assert.Equal(_businessId, verified.Data!.BusinessId);
        Assert.Equal(3600, verified.Data.ExpiresIn);
        Assert.Equal("254712345678", verified.Data.Customer.PhoneNumber);
        Assert.Equal("g•••@test.com", verified.Data.Customer.Email);
        // No internal identifier is exposed.
        Assert.DoesNotContain(
            "userId",
            System.Text.Json.JsonSerializer.Serialize(verified.Data),
            StringComparison.OrdinalIgnoreCase);

        // The token is an ordinary customer token for this business.
        var token = new JwtSecurityTokenHandler().ReadJwtToken(verified.Data.AccessToken);
        Assert.Contains(token.Claims, claim =>
            claim.Type.EndsWith("role", StringComparison.Ordinal) && claim.Value == "Customer");
        Assert.Contains(token.Claims, claim =>
            claim.Type == "biz" && claim.Value == _businessId.ToString());

        // The identity is now verified, and the code is spent.
        var auth = await context.UserAuths.AsNoTracking()
            .SingleAsync(a => a.Email == "guest@test.com");
        Assert.True(auth.IsVerified);
        Assert.Null(auth.VerificationCode);

        // Replaying the same code cannot mint a second session.
        var replay = await service.VerifyAsync(_businessId, Verify(code));
        Assert.Equal("CODE_INVALID", replay.Error?.Code);
    }

    [Fact]
    public async Task Nothing_works_without_the_booking_module_or_across_a_tenant_boundary()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = await SeededAsync(connection);
        var email = new RecordingEmailService();

        // A business that never bought the appointments module.
        var disabled = Service(context, email, new StubModuleEntitlements());
        Assert.Equal("MODULE_DISABLED", (await disabled.StartAsync(_businessId, Start())).Error?.Code);

        // An unknown business is indistinguishable from a missing one.
        var unknown = await Service(context, email).StartAsync(Guid.NewGuid(), Start());
        Assert.Equal("NOT_FOUND", unknown.Error?.Code);

        // A tenant host that names a different business cannot book there.
        var scoped = Service(context, email, tenant: new FixedTenantContext(Guid.NewGuid()));
        Assert.Equal("NOT_FOUND", (await scoped.StartAsync(_businessId, Start())).Error?.Code);
    }

    /// <summary>A tenant context pinned to a business other than the one requested.</summary>
    private sealed class FixedTenantContext : ITenantContext
    {
        public FixedTenantContext(Guid businessId) => BusinessId = businessId;

        public string? Slug => "other-business";
        public Guid? BusinessId { get; }
        public bool IsActive => BusinessId.HasValue;
    }
}
