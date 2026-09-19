using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.Attendance;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Tests;

/// <summary>
/// I1–I3 (plan §18.5): same key + body replays the stored response
/// (first-response-wins); same key + different body → 409 IDEMPOTENCY_CONFLICT;
/// idempotency-storage failure never breaks the primary operation (§11).
/// The request hash includes the VERB, so a clock-in replay can never answer
/// a clock-out.
/// </summary>
public class AttendanceIdempotencyTests
{
    private sealed class ThrowingIdempotencyService : IIdempotencyService
    {
        public Task<IdempotencyLookupResult> TryGetAsync(string key, Guid userId, string requestHash) =>
            throw new InvalidOperationException("idempotency store unavailable");

        public Task StoreAsync(string key, Guid userId, string requestHash, string responseJson) =>
            throw new InvalidOperationException("idempotency store unavailable");
    }

    [Fact]
    public async Task I1_SameKeyAndBody_ReplaysStoredResponse()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await AttendanceServiceTests.CreateEnvAsync(connection);
        using var context = env.Context;

        var first = await env.Service.ClockInAsync(env.Staff.Id,
            new AttendanceClockRequest { Token = env.RawToken }, idempotencyKey: "retry-key");
        Assert.True(first.Success, first.Error?.Message);

        // Simulate a network retry: same key + body.
        context.ChangeTracker.Clear();
        var second = await env.Service.ClockInAsync(env.Staff.Id,
            new AttendanceClockRequest { Token = env.RawToken }, idempotencyKey: "retry-key");

        Assert.True(second.Success, second.Error?.Message);
        Assert.Equal(first.Data!.State, second.Data!.State);
        Assert.Equal(first.Data.OpenedAt, second.Data.OpenedAt);

        // No second ledger row: the replay never re-executed the pipeline.
        context.ChangeTracker.Clear();
        Assert.Equal(1, await context.AttendanceEvents.CountAsync());
        Assert.Equal(1, await context.AttendanceSessions.CountAsync());

        // The stored entry is the serialized first response (24 h lifetime).
        var entry = await context.IdempotencyKeys.SingleAsync();
        Assert.Equal("retry-key", entry.Key);
        Assert.Equal(env.Staff.Id, entry.UserId);
        Assert.Contains("clocked_in", entry.ResponseJson);
    }

    [Fact]
    public async Task I2_SameKeyDifferentBody_ReturnsIdempotencyConflict()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await AttendanceServiceTests.CreateEnvAsync(connection);
        using var context = env.Context;

        Assert.True((await env.Service.ClockInAsync(env.Staff.Id,
            new AttendanceClockRequest { Token = env.RawToken }, "same-key")).Success);
        context.ChangeTracker.Clear();

        var conflict = await env.Service.ClockInAsync(env.Staff.Id,
            new AttendanceClockRequest { Token = "punched:attendance:v1:different-body" }, "same-key");

        Assert.False(conflict.Success);
        Assert.Equal("IDEMPOTENCY_CONFLICT", conflict.Error!.Code);
        context.ChangeTracker.Clear();
        Assert.Equal(1, await context.AttendanceEvents.CountAsync());
    }

    [Fact]
    public async Task I3_StorageFailure_NeverBreaksThePrimaryOperation()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await AttendanceServiceTests.CreateEnvAsync(connection);
        using var context = env.Context;

        // A completely broken idempotency store must not stop clocking (§11).
        var broken = new AttendanceService(
            context,
            new AttendancePolicyService(context),
            new PunchedApi.Application.Attendance.Verification.AttendanceVerificationEngine(
                new PunchedApi.Application.Attendance.Verification.IAttendanceVerifier[]
                {
                    new PunchedApi.Application.Attendance.Verification.AuthenticatedUserVerifier(context),
                    new PunchedApi.Application.Attendance.Verification.QrVerifier(context),
                },
                TestHelpers.CreateLogger<PunchedApi.Application.Attendance.Verification.AttendanceVerificationEngine>()),
            new ThrowingIdempotencyService(),
            TestHelpers.CreateLogger<AttendanceService>());

        var result = await broken.ClockInAsync(env.Staff.Id,
            new AttendanceClockRequest { Token = env.RawToken }, idempotencyKey: "some-key");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("clocked_in", result.Data!.State);

        context.ChangeTracker.Clear();
        Assert.Equal(1, await context.AttendanceSessions.CountAsync());
        Assert.Empty(await context.IdempotencyKeys.ToListAsync());
    }
}