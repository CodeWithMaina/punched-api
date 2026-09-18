using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.Attendance;
using PunchedApi.Application.Attendance.Verification;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Tests;

/// <summary>
/// Phase 2 policy reads + validation helpers (plan §6.1, §15.2). A missing row
/// is the implicit Standard default — never an error — and the validation
/// helpers return the §9.6 error codes consumed by the Phase 4 write path.
/// </summary>
public class AttendancePolicyServiceTests
{
    private static async Task<(ApplicationDbContext Context, Guid BusinessId)> CreateEnvAsync(SqliteConnection connection)
    {
        var context = BookingTestBase.CreateContext(connection);
        var owner = BookingTestBase.CreateOwner("owner@policy.test");
        var business = BookingTestBase.CreateBusiness(owner.Id, "Policy Biz");
        await BookingTestBase.SeedAsync(context, owner, business);
        context.ChangeTracker.Clear();
        return (context, business.Id);
    }

    [Fact]
    public async Task GetEffectivePolicy_WithNoRow_ReturnsImplicitStandardDefault()
    {
        using var connection = BookingTestBase.CreateConnection();
        var (context, businessId) = await CreateEnvAsync(connection);
        using var _ = context;

        var service = new AttendancePolicyService(context);
        var policy = await service.GetEffectivePolicyAsync(businessId);

        Assert.Equal(AttendanceMode.Standard, policy.Mode);
        Assert.True(policy.IsActive);
        Assert.Null(policy.UpdatedAt);
        Assert.Equal(
            new[] { AttendanceVerificationMethod.AuthenticatedUser, AttendanceVerificationMethod.Qr },
            AttendanceVerification.ParseRequiredMethods(policy.RequiredVerificationsJson));

        // The default is served, never written: no row was created (§6.1).
        Assert.Empty(await context.AttendancePolicies.ToListAsync());
    }

    [Fact]
    public async Task GetEffectivePolicy_WithARow_ReturnsPersistedValues()
    {
        using var connection = BookingTestBase.CreateConnection();
        var (context, businessId) = await CreateEnvAsync(connection);
        using var _ = context;

        await BookingTestBase.SeedAsync(context, new AttendancePolicy
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            Mode = AttendanceMode.Standard,
            RequiredVerificationsJson = "[\"QR\"]",
            IsActive = false,
            UpdatedAt = DateTime.UtcNow,
        });
        context.ChangeTracker.Clear();

        var policy = await new AttendancePolicyService(context).GetEffectivePolicyAsync(businessId);

        Assert.False(policy.IsActive);
        Assert.Equal("[\"QR\"]", policy.RequiredVerificationsJson);
        Assert.NotNull(policy.UpdatedAt);
    }

    [Fact]
    public async Task GetPolicyAsync_ReturnsScreamingSnakeWireValues()
    {
        using var connection = BookingTestBase.CreateConnection();
        var (context, businessId) = await CreateEnvAsync(connection);
        using var _ = context;

        await BookingTestBase.SeedAsync(context, new AttendancePolicy
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            // Deliberately out of order + an unknown value: the response is
            // canonicalised (enum order, unknown skipped).
            RequiredVerificationsJson = "[\"QR\",\"NOT_A_METHOD\",\"AUTHENTICATED_USER\"]",
            Mode = AttendanceMode.Standard,
            IsActive = true,
        });
        context.ChangeTracker.Clear();

        var response = await new AttendancePolicyService(context).GetPolicyAsync(businessId);

        Assert.True(response.Success, response.Error?.Message);
        Assert.Equal("STANDARD", response.Data!.Mode);
        Assert.Equal(new[] { "AUTHENTICATED_USER", "QR" }, response.Data.RequiredVerifications);
        Assert.True(response.Data.IsActive);
        Assert.Equal(AttendancePolicyService.DefaultMaxOpenSessionHours, response.Data.MaxOpenSessionHours);
    }

    // ── Validation helpers (the Phase 4 write path consumes these) ──

    [Fact]
    public void ValidateMode_AcceptsOnlyStandard_InV1()
    {
        Assert.Null(AttendancePolicyService.ValidateMode("STANDARD"));
        Assert.Null(AttendancePolicyService.ValidateMode("standard"));
        Assert.Equal("INVALID_MODE", AttendancePolicyService.ValidateMode("ROTATING_QR"));
        Assert.Equal("INVALID_MODE", AttendancePolicyService.ValidateMode(null));
    }

    [Fact]
    public void ValidateVerificationSet_RequiresKnownNonEmptySetContainingAuthenticatedUser()
    {
        Assert.Null(AttendancePolicyService.ValidateVerificationSet(new[] { "AUTHENTICATED_USER", "QR" }));
        Assert.Null(AttendancePolicyService.ValidateVerificationSet(new[] { "AUTHENTICATED_USER" }));

        Assert.Equal("INVALID_VERIFICATION_SET", AttendancePolicyService.ValidateVerificationSet(Array.Empty<string>()));
        Assert.Equal("INVALID_VERIFICATION_SET", AttendancePolicyService.ValidateVerificationSet(null));
        Assert.Equal("INVALID_VERIFICATION_SET", AttendancePolicyService.ValidateVerificationSet(new[] { "QR" }));
        Assert.Equal("INVALID_VERIFICATION_SET", AttendancePolicyService.ValidateVerificationSet(new[] { "AUTHENTICATED_USER", "NOPE" }));
    }

    [Fact]
    public void ValidateSessionWindow_AllowsNullOrFourToFortyEightHours()
    {
        Assert.Null(AttendancePolicyService.ValidateSessionWindow(null));
        Assert.Null(AttendancePolicyService.ValidateSessionWindow(4));
        Assert.Null(AttendancePolicyService.ValidateSessionWindow(16));
        Assert.Null(AttendancePolicyService.ValidateSessionWindow(48));

        Assert.Equal("INVALID_SESSION_WINDOW", AttendancePolicyService.ValidateSessionWindow(3));
        Assert.Equal("INVALID_SESSION_WINDOW", AttendancePolicyService.ValidateSessionWindow(49));
    }

    [Fact]
    public void SerializeVerifications_WritesCanonicalJsonWithoutDuplicates()
    {
        var json = AttendancePolicyService.SerializeVerifications(
            new[] { "QR", "AUTHENTICATED_USER", "QR", "NOT_A_METHOD" });

        Assert.Equal("[\"AUTHENTICATED_USER\",\"QR\"]", json);
    }
}