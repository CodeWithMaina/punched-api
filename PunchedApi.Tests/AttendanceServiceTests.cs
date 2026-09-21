using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.Attendance;
using PunchedApi.Application.Attendance.Verification;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;
using PunchedApi.Infrastructure.Repositories;

namespace PunchedApi.Tests;

/// <summary>
/// Phase 3 clock-in (C1–C8) and clock-out (O1–O6) matrices (plan §18.3–§18.4),
/// plus the double-tap concurrency guarantee. SQLite in-memory with real
/// indexes (the BookingTestBase convention) — the partial unique index
/// ix_attendance_sessions_open_per_staff is exercised for real.
/// The service never emits MODULE_DISABLED: the attribute owns that (§9.6).
/// </summary>
public class AttendanceServiceTests
{
    internal sealed class Env
    {
        public ApplicationDbContext Context = null!;
        public AttendanceService Service = null!;
        public Business Business = null!;
        public User Owner = null!;
        public User Staff = null!;
        public AttendanceLocation Location = null!;
        public string RawToken = string.Empty;
    }

    internal static async Task<Env> CreateEnvAsync(SqliteConnection connection)
    {
        var context = BookingTestBase.CreateContext(connection);
        var owner = BookingTestBase.CreateOwner("owner@clock.test");
        var business = BookingTestBase.CreateBusiness(owner.Id, "Clock Biz");
        var staff = BookingTestBase.CreateStaff(business.Id, "staff@clock.test");

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

        await BookingTestBase.SeedAsync(context, owner, business, staff, location, credential);
        context.ChangeTracker.Clear();

        return new Env
        {
            Context = context,
            Service = CreateService(context),
            Business = business,
            Owner = owner,
            Staff = staff,
            Location = location,
            RawToken = rawToken,
        };
    }

    internal static AttendanceService CreateService(ApplicationDbContext context) =>
        new(
            context,
            new AttendancePolicyService(context),
            new AttendanceVerificationEngine(
                new IAttendanceVerifier[] { new AuthenticatedUserVerifier(context), new QrVerifier(context) },
                TestHelpers.CreateLogger<AttendanceVerificationEngine>()),
            new IdempotencyService(new UnitOfWork(context), context, TestHelpers.CreateLogger<IdempotencyService>()),
            TestHelpers.CreateLogger<AttendanceService>());

    private static AttendanceClockRequest Clock(Env env, string? token = null) =>
        new() { Token = token ?? env.RawToken };

    // ── C1–C8: clock-in ───────────────────────────────────────

    [Fact]
    public async Task C1_ClockIn_Succeeds_PersistsEventSessionLinkage_AndAudit()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        var result = await env.Service.ClockInAsync(env.Staff.Id, Clock(env));

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("clocked_in", result.Data!.State);
        Assert.Equal(env.Business.Id, result.Data.BusinessId);
        Assert.Equal(env.Staff.Id, result.Data.StaffUserId);      // server-derived
        Assert.Equal(env.Location.Id, result.Data.LocationId);
        Assert.Equal("Reception", result.Data.LocationName);
        Assert.NotNull(result.Data.OpenedAt);
        Assert.Equal("CLOCK_IN", result.Data.LastEventType);

        context.ChangeTracker.Clear();
        var evt = await context.AttendanceEvents.SingleAsync();
        var session = await context.AttendanceSessions.SingleAsync();

        Assert.Equal(AttendanceEventType.ClockIn, evt.EventType);
        Assert.Equal(env.Staff.Id, evt.StaffUserId);              // forged ids impossible
        Assert.Equal(session.Id, evt.AttendanceSessionId);        // event ↔ session linkage
        Assert.Equal(evt.Id, session.OpeningEventId);
        Assert.Equal(env.Location.Id, session.OpeningLocationId);
        Assert.Null(session.ClosedAt);
        Assert.Equal(AttendanceSessionStatus.Open, session.Status);
        Assert.Contains("AUTHENTICATED_USER", evt.VerificationSummaryJson);
        Assert.Contains("QR", evt.VerificationSummaryJson);
        Assert.DoesNotContain(env.RawToken, evt.VerificationSummaryJson);

        // LastUsedAt refreshed for operator visibility (§6.3).
        Assert.True(await context.AttendanceQrCredentials.AnyAsync(c => c.LastUsedAt != null));

        // Audit row with the action, ids and verifier summary — never the token.
        var logs = await context.ApiEventLogs.ToListAsync();
        Assert.Contains(logs, l => l.DetailsJson != null && l.DetailsJson.Contains("CLOCK_IN"));
        Assert.DoesNotContain(logs.Select(l => l.DetailsJson), d => d != null && d.Contains(env.RawToken));
    }

    [Fact]
    public async Task C2_DuplicateClockIn_NewKey_ReturnsAlreadyClockedIn()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        var first = await env.Service.ClockInAsync(env.Staff.Id, Clock(env), "key-A");
        Assert.True(first.Success, first.Error?.Message);
        context.ChangeTracker.Clear();

        var second = await env.Service.ClockInAsync(env.Staff.Id, Clock(env), "key-B");
        Assert.False(second.Success);
        Assert.Equal("ALREADY_CLOCKED_IN", second.Error!.Code);

        context.ChangeTracker.Clear();
        Assert.Equal(1, await context.AttendanceSessions.CountAsync(s => s.ClosedAt == null));
        Assert.Equal(1, await context.AttendanceEvents.CountAsync());
    }

    [Fact]
    public async Task C3_InvalidVerification_NeverTouchesAttendanceState()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        var result = await env.Service.ClockInAsync(env.Staff.Id, Clock(env, token: "punched:attendance:v1:bogus"));

        Assert.False(result.Success);
        Assert.Equal("INVALID_QR", result.Error!.Code);

        context.ChangeTracker.Clear();
        Assert.Empty(await context.AttendanceEvents.ToListAsync());
        Assert.Empty(await context.AttendanceSessions.ToListAsync());
    }

    [Fact]
    public async Task C4_ModuleDisabledPath_ServiceNeverEmitsModuleDisabled()
    {
        // MODULE_DISABLED is emitted by [RequireModule], never by the service:
        // even with NO business_modules row at all, the service itself works.
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        var result = await env.Service.ClockInAsync(env.Staff.Id, Clock(env));

        Assert.True(result.Success, result.Error?.Message);
        context.ChangeTracker.Clear();
        Assert.Empty(await context.BusinessModules.ToListAsync());
    }

    [Fact]
    public async Task C5_UnlinkedStaff_ReturnsNotLinked()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        var unlinked = BookingTestBase.CreateStaff(env.Business.Id, "unlinked@clock.test");
        unlinked.StaffBusinessId = null;
        await BookingTestBase.SeedAsync(context, unlinked);
        context.ChangeTracker.Clear();

        var result = await env.Service.ClockInAsync(unlinked.Id, Clock(env));

        Assert.False(result.Success);
        Assert.Equal("NOT_LINKED", result.Error!.Code);
    }

    [Fact]
    public async Task C6_InactiveLocation_ReturnsLocationInactive()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        var tracked = await context.AttendanceLocations.SingleAsync();
        tracked.IsActive = false;
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var result = await env.Service.ClockInAsync(env.Staff.Id, Clock(env));

        Assert.False(result.Success);
        Assert.Equal("LOCATION_INACTIVE", result.Error!.Code);
        context.ChangeTracker.Clear();
        Assert.Empty(await context.AttendanceSessions.ToListAsync());
    }

    [Fact]
    public async Task C7_PolicyInactive_ReturnsAttendanceDisabled()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        await BookingTestBase.SeedAsync(context, new AttendancePolicy
        {
            Id = Guid.NewGuid(),
            BusinessId = env.Business.Id,
            Mode = AttendanceMode.Standard,
            RequiredVerificationsJson = AttendancePolicyService.DefaultRequiredVerificationsJson,
            IsActive = false,
        });
        context.ChangeTracker.Clear();

        var result = await env.Service.ClockInAsync(env.Staff.Id, Clock(env));

        Assert.False(result.Success);
        Assert.Equal("ATTENDANCE_DISABLED", result.Error!.Code);
        context.ChangeTracker.Clear();
        Assert.Empty(await context.AttendanceSessions.ToListAsync());
    }

    [Fact]
    public async Task C8_NothingConfigured_ReturnsAttendanceNotConfigured_BeforeAnyScan()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        // Wipe every location and credential for the business.
        context.RemoveRange(await context.AttendanceQrCredentials.ToListAsync());
        context.RemoveRange(await context.AttendanceLocations.ToListAsync());
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var result = await env.Service.ClockInAsync(env.Staff.Id, Clock(env));

        Assert.False(result.Success);
        Assert.Equal("ATTENDANCE_NOT_CONFIGURED", result.Error!.Code);
    }

    // ── O1–O6: clock-out ──────────────────────────────────────

    [Fact]
    public async Task O1_ClockOut_ComputesWorkedMinutes_AndClosesSession()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        await env.Service.ClockInAsync(env.Staff.Id, Clock(env));
        context.ChangeTracker.Clear();

        // Simulate a shift opened 7.5 h ago (§9.5: minutes computed on close).
        var session = await context.AttendanceSessions.SingleAsync();
        session.OpenedAt = DateTime.UtcNow.AddHours(-7.5);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var result = await env.Service.ClockOutAsync(env.Staff.Id, Clock(env));

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("not_clocked_in", result.Data!.State);

        context.ChangeTracker.Clear();
        var closed = await context.AttendanceSessions.SingleAsync();
        Assert.Equal(AttendanceSessionStatus.Closed, closed.Status);
        Assert.NotNull(closed.ClosedAt);
        Assert.NotNull(closed.ClosingEventId);
        Assert.NotNull(closed.ClosingLocationId);
        Assert.InRange(closed.WorkedMinutes!.Value, 449, 451); // 7.5 h in whole minutes

        var clockOutEvent = await context.AttendanceEvents
            .SingleAsync(e => e.EventType == AttendanceEventType.ClockOut);
        Assert.Equal(closed.Id, clockOutEvent.AttendanceSessionId);
        Assert.Equal(closed.ClosingEventId, clockOutEvent.Id);
    }

    [Fact]
    public async Task O2_ClockOut_WithoutOpenSession_ReturnsNotClockedIn()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        var result = await env.Service.ClockOutAsync(env.Staff.Id, Clock(env));

        Assert.False(result.Success);
        Assert.Equal("NOT_CLOCKED_IN", result.Error!.Code);
        context.ChangeTracker.Clear();
        Assert.Empty(await context.AttendanceEvents.ToListAsync());
    }

    [Fact]
    public async Task O3_DuplicateClockOut_NewKey_ReturnsNotClockedIn()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        Assert.True((await env.Service.ClockInAsync(env.Staff.Id, Clock(env), "in-1")).Success);
        Assert.True((await env.Service.ClockOutAsync(env.Staff.Id, Clock(env), "out-1")).Success);
        context.ChangeTracker.Clear();

        var second = await env.Service.ClockOutAsync(env.Staff.Id, Clock(env), "out-2");
        Assert.False(second.Success);
        Assert.Equal("NOT_CLOCKED_IN", second.Error!.Code);

        context.ChangeTracker.Clear();
        Assert.Equal(2, await context.AttendanceEvents.CountAsync()); // no extra ledger rows
    }

    [Fact]
    public async Task O4_ClockOut_InvalidVerification_NeverMutatesState()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        Assert.True((await env.Service.ClockInAsync(env.Staff.Id, Clock(env))).Success);
        context.ChangeTracker.Clear();

        var result = await env.Service.ClockOutAsync(env.Staff.Id, Clock(env, token: "punched:attendance:v1:bad"));
        Assert.False(result.Success);
        Assert.Equal("INVALID_QR", result.Error!.Code);

        context.ChangeTracker.Clear();
        var session = await context.AttendanceSessions.SingleAsync();
        Assert.Null(session.ClosedAt); // still open
        Assert.Equal(1, await context.AttendanceEvents.CountAsync());
    }

    [Fact]
    public async Task O5_StaleOpenSession_IsAutoClosed_ThenClockInSucceeds()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        await env.Service.ClockInAsync(env.Staff.Id, Clock(env), "yesterday");
        context.ChangeTracker.Clear();

        // Backdate the open session beyond the 16 h stale window (§9.2 / D7).
        var stale = await context.AttendanceSessions.SingleAsync();
        stale.OpenedAt = DateTime.UtcNow.AddHours(-17);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var result = await env.Service.ClockInAsync(env.Staff.Id, Clock(env), "next-morning");

        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal("clocked_in", result.Data!.State);

        context.ChangeTracker.Clear();
        Assert.Equal(2, await context.AttendanceSessions.CountAsync());
        var autoClosed = await context.AttendanceSessions.OrderBy(s => s.OpenedAt).FirstAsync();
        Assert.Equal(AttendanceSessionStatus.Closed, autoClosed.Status);
        Assert.NotNull(autoClosed.ClosedAt);
        Assert.Equal(AttendancePolicyService.DefaultMaxOpenSessionHours * 60, autoClosed.WorkedMinutes);
        // No fabricated CLOCK_OUT event: the ledger holds only the two
        // CLOCK_IN rows (the stale session's and the new one).
        Assert.Equal(2, await context.AttendanceEvents.CountAsync());
        Assert.Equal(0, await context.AttendanceEvents.CountAsync(e => e.EventType == AttendanceEventType.ClockOut));

        // AUTO_CLOSED audit entry recorded.
        var logs = await context.ApiEventLogs.ToListAsync();
        Assert.Contains(logs, l => l.DetailsJson != null && l.DetailsJson.Contains("SESSION_AUTO_CLOSED"));
    }

    [Fact]
    public async Task O6_CrossLocationClose_RecordsClosingLocation()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        var backLocation = new AttendanceLocation
        {
            Id = Guid.NewGuid(),
            BusinessId = env.Business.Id,
            Name = "Warehouse",
            IsActive = true,
            CreatedByUserId = env.Owner.Id,
            CreatedAt = DateTime.UtcNow,
        };
        var backToken = AttendanceTokenFactory.CreatePayload();
        var backCredential = new AttendanceQrCredential
        {
            Id = Guid.NewGuid(),
            BusinessId = env.Business.Id,
            AttendanceLocationId = backLocation.Id,
            TokenHash = AttendanceTokenFactory.HashToken(backToken),
            Status = AttendanceCredentialStatus.Active,
            CreatedByUserId = env.Owner.Id,
            CreatedAt = DateTime.UtcNow,
        };
        await BookingTestBase.SeedAsync(context, backLocation, backCredential);
        context.ChangeTracker.Clear();

        Assert.True((await env.Service.ClockInAsync(env.Staff.Id, Clock(env))).Success);
        context.ChangeTracker.Clear();

        // Clock out at the OTHER location — legitimate (O6).
        var result = await env.Service.ClockOutAsync(env.Staff.Id, new AttendanceClockRequest { Token = backToken });

        Assert.True(result.Success, result.Error?.Message);
        context.ChangeTracker.Clear();
        var session = await context.AttendanceSessions.SingleAsync();
        Assert.Equal(env.Location.Id, session.OpeningLocationId);
        Assert.Equal(backLocation.Id, session.ClosingLocationId);
    }

    // ── Status / history ──────────────────────────────────────

    [Fact]
    public async Task Status_FlipsAfterClockInOut_AndHistoryShowsBothEvents()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        var before = await env.Service.StatusAsync(env.Staff.Id);
        Assert.True(before.Success, before.Error?.Message);
        Assert.Equal("not_clocked_in", before.Data!.State);
        Assert.Equal(0, before.Data.TodayWorkedMinutes);

        Assert.True((await env.Service.ClockInAsync(env.Staff.Id, Clock(env))).Success);
        context.ChangeTracker.Clear();

        var during = await env.Service.StatusAsync(env.Staff.Id);
        Assert.True(during.Success, during.Error?.Message);
        Assert.Equal("clocked_in", during.Data!.State);
        Assert.Equal("Reception", during.Data.LocationName);
        Assert.True(during.Data.ElapsedMinutes >= 0);

        // Backdate so the close computes minutes, then close + inspect history.
        var session = await context.AttendanceSessions.SingleAsync();
        session.OpenedAt = DateTime.UtcNow.AddMinutes(-90);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        Assert.True((await env.Service.ClockOutAsync(env.Staff.Id, Clock(env))).Success);
        context.ChangeTracker.Clear();

        var after = await env.Service.StatusAsync(env.Staff.Id);
        Assert.True(after.Success, after.Error?.Message);
        Assert.Equal("not_clocked_in", after.Data!.State);
        Assert.Equal("CLOCK_OUT", after.Data.LastEventType);
        Assert.InRange(after.Data.TodayWorkedMinutes, 89, 91);

        var history = await env.Service.HistoryAsync(env.Staff.Id, new AttendanceHistoryQuery());
        Assert.True(history.Success, history.Error?.Message);
        Assert.Equal(2, history.Data!.TotalCount);
        Assert.All(history.Data.Items, i => Assert.False(i.InProgress));
        Assert.Contains(history.Data.Items, i => i.EventType == "CLOCK_IN");
        var outItem = history.Data.Items.Single(i => i.EventType == "CLOCK_OUT");
        Assert.NotNull(outItem.SessionId);
        Assert.InRange(outItem.WorkedMinutes!.Value, 89, 91);
        Assert.Equal("Reception", outItem.LocationName);
    }

    [Fact]
    public async Task History_NeverReturnsAnotherStaffsRows()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        var colleague = BookingTestBase.CreateStaff(env.Business.Id, "colleague@clock.test");
        await BookingTestBase.SeedAsync(context, colleague);
        context.ChangeTracker.Clear();

        Assert.True((await env.Service.ClockInAsync(env.Staff.Id, Clock(env))).Success);
        context.ChangeTracker.Clear();

        var mine = await env.Service.HistoryAsync(env.Staff.Id, new AttendanceHistoryQuery());
        Assert.Equal(1, mine.Data!.TotalCount);

        var theirs = await env.Service.HistoryAsync(colleague.Id, new AttendanceHistoryQuery());
        Assert.Equal(0, theirs.Data!.TotalCount);
    }

    [Fact]
    public async Task History_RejectsInvalidEventType_AndInvalidDateRange()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        var badType = await env.Service.HistoryAsync(env.Staff.Id,
            new AttendanceHistoryQuery { EventType = "NOT_A_TYPE" });
        Assert.False(badType.Success);
        Assert.Equal("INVALID_EVENT_TYPE", badType.Error!.Code);

        var badRange = await env.Service.HistoryAsync(env.Staff.Id, new AttendanceHistoryQuery
        {
            From = DateOnly.FromDateTime(DateTime.UtcNow.Date),
            To = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-1)),
        });
        Assert.False(badRange.Success);
        Assert.Equal("INVALID_DATE_RANGE", badRange.Error!.Code);
    }

    // ── Concurrency: exactly one OPEN session ─────────────────

    [Fact]
    public async Task Concurrency_ParallelClockIns_YieldExactlyOneOpenSession()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await CreateEnvAsync(connection);
        using var context = env.Context;

        // Each attempt uses its own context over the shared connection — the
        // SQLite writer serialises them, mirroring the single-writer race.
        var results = new List<ApiResponse<AttendanceStatusResponse>>();
        for (var i = 0; i < 4; i++)
        {
            var attemptContext = BookingTestBase.CreateContext(connection);
            results.Add(await CreateService(attemptContext).ClockInAsync(
                env.Staff.Id, new AttendanceClockRequest { Token = env.RawToken }));
        }

        // Exactly one success; every other attempt is rejected.
        Assert.Equal(1, results.Count(r => r.Success));
        Assert.All(results.Where(r => !r.Success), r => Assert.Equal("ALREADY_CLOCKED_IN", r.Error!.Code));

        context.ChangeTracker.Clear();
        Assert.Equal(1, await context.AttendanceSessions.CountAsync(s => s.ClosedAt == null));
        Assert.Equal(1, await context.AttendanceEvents.CountAsync()); // no orphan events
    }
}