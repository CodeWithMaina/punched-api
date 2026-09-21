using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PunchedApi.API.Controllers;
using PunchedApi.Application.Attendance;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Tests;

/// <summary>
/// Security matrix S1–S6 (plan §18.6): cross-org QR answers an actionable
/// code but never leaks state; there is NO client-supplied staff/business id
/// anywhere in the request contract; deleted staff ⇒ FORBIDDEN; Customer and
/// Admin roles are rejected; an unauthenticated principal ⇒ 401 from the
/// controller. Also covers the controller's §9.6 error-code → HTTP switch.
/// </summary>
public class AttendancePermissionTests
{
    // ── S1: cross-organisation QR ─────────────────────────────

    [Fact]
    public async Task S1_CrossOrgQr_ReturnsQrWrongOrganization_AndMutatesNothing()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await AttendanceServiceTests.CreateEnvAsync(connection);
        using var context = env.Context;

        // A second business + staff member with their own location + QR.
        var otherOwner = BookingTestBase.CreateOwner("other-owner@clock.test");
        var otherBusiness = BookingTestBase.CreateBusiness(otherOwner.Id, "Other Biz");
        var otherStaff = BookingTestBase.CreateStaff(otherBusiness.Id, "other-staff@clock.test");
        var otherLocation = new AttendanceLocation
        {
            Id = Guid.NewGuid(),
            BusinessId = otherBusiness.Id,
            Name = "Other Door",
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
        await BookingTestBase.SeedAsync(context, otherOwner, otherBusiness, otherStaff, otherLocation, otherCredential);
        context.ChangeTracker.Clear();

        // Staff of biz A scans biz B's printed QR.
        var result = await env.Service.ClockInAsync(env.Staff.Id, new AttendanceClockRequest { Token = otherToken });
        Assert.False(result.Success);
        Assert.Equal("QR_WRONG_ORGANIZATION", result.Error!.Code); // actionable, not a probing oracle

        // And the reverse direction: staff of biz B scans biz A's QR.
        var reverse = await AttendanceServiceTests.CreateService(context).ClockInAsync(
            otherStaff.Id, new AttendanceClockRequest { Token = env.RawToken });
        Assert.False(reverse.Success);
        Assert.Equal("QR_WRONG_ORGANIZATION", reverse.Error!.Code);

        // Nothing was persisted for either attempt.
        context.ChangeTracker.Clear();
        Assert.Empty(await context.AttendanceEvents.ToListAsync());
        Assert.Empty(await context.AttendanceSessions.ToListAsync());
    }

    // ── S2: no forged identity fields exist ───────────────────

    [Fact]
    public async Task S2_ForgedStaffId_IsImpossible_DtoCarriesOnlyTheToken()
    {
        // The request contract has exactly ONE property ("token") — there is
        // no staffId / businessId field a caller could forge (§10.2).
        var property = Assert.Single(typeof(AttendanceClockRequest).GetProperties());
        Assert.Equal("Token", property.Name);
        Assert.Equal("token", property.GetCustomAttributes(false)
            .OfType<System.Text.Json.Serialization.JsonPropertyNameAttribute>()
            .Single().Name);

        // And whatever the caller sends, the persisted staff_user_id is the
        // authenticated actor's id, never anything else.
        using var connection = BookingTestBase.CreateConnection();
        var env = await AttendanceServiceTests.CreateEnvAsync(connection);
        using var context = env.Context;

        var result = await env.Service.ClockInAsync(env.Staff.Id,
            new AttendanceClockRequest { Token = env.RawToken });
        Assert.True(result.Success, result.Error?.Message);
        Assert.Equal(env.Staff.Id, result.Data!.StaffUserId);

        context.ChangeTracker.Clear();
        var evt = await context.AttendanceEvents.SingleAsync();
        Assert.Equal(env.Staff.Id, evt.StaffUserId);
        var session = await context.AttendanceSessions.SingleAsync();
        Assert.Equal(env.Staff.Id, session.StaffUserId);
    }

    // ── S3: deleted staff ─────────────────────────────────────

    [Fact]
    public async Task S3_DeletedStaff_ReturnsForbidden()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await AttendanceServiceTests.CreateEnvAsync(connection);
        using var context = env.Context;

        // Soft-delete the staff member (the User global query filter hides the row).
        var tracked = await context.Users.SingleAsync(u => u.Id == env.Staff.Id);
        tracked.IsDeleted = true;
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var result = await env.Service.ClockInAsync(env.Staff.Id,
            new AttendanceClockRequest { Token = env.RawToken });

        Assert.False(result.Success);
        Assert.Equal("FORBIDDEN", result.Error!.Code);
        context.ChangeTracker.Clear();
        Assert.Empty(await context.AttendanceSessions.ToListAsync());
    }

    // ── S4/S5: Customer / Admin roles ─────────────────────────

    [Fact]
    public async Task S4_CustomerRole_ReturnsForbidden()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await AttendanceServiceTests.CreateEnvAsync(connection);
        using var context = env.Context;

        var customer = BookingTestBase.CreateCustomer("customer@clock.test");
        await BookingTestBase.SeedAsync(context, customer);
        context.ChangeTracker.Clear();

        var result = await env.Service.ClockInAsync(customer.Id,
            new AttendanceClockRequest { Token = env.RawToken });

        Assert.False(result.Success);
        Assert.Equal("FORBIDDEN", result.Error!.Code);
    }

    [Fact]
    public async Task S5_AdminRole_ReturnsForbidden_OnTheServicePath()
    {
        using var connection = BookingTestBase.CreateConnection();
        var env = await AttendanceServiceTests.CreateEnvAsync(connection);
        using var context = env.Context;

        // The DB role is the authority — a stale/widened JWT role claim can
        // never grant attendance (the service re-derives the role from Users).
        var admin = BookingTestBase.CreateOwner("admin@clock.test");
        admin.Role = UserRole.Admin;
        await BookingTestBase.SeedAsync(context, admin);
        context.ChangeTracker.Clear();

        var result = await env.Service.ClockInAsync(admin.Id,
            new AttendanceClockRequest { Token = env.RawToken });

        Assert.False(result.Success);
        Assert.Equal("FORBIDDEN", result.Error!.Code);
    }

    // ── S6: unauthenticated ───────────────────────────────────

    [Fact]
    public async Task S6_Unauthenticated_Returns401_BeforeAnyServiceWork()
    {
        // Controller-level: a principal with no userId claim → 401, and the
        // (throwing) stub service is never reached.
        var controller = new AttendanceController(ThrowingAttendanceService.Instance);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext(),
        };
        controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity()); // unauthenticated

        Assert.IsType<UnauthorizedResult>(await controller.Status());
        Assert.IsType<UnauthorizedResult>(await controller.ClockIn(new AttendanceClockRequest { Token = "x" }));
        Assert.IsType<UnauthorizedResult>(await controller.ClockOut(new AttendanceClockRequest { Token = "x" }));
        Assert.IsType<UnauthorizedResult>(await controller.History(new AttendanceHistoryQuery()));
    }

    /// <summary>A service stub that throws if the controller ever calls it — proving 401 short-circuits.</summary>
    private sealed class ThrowingAttendanceService : IAttendanceService
    {
        public static readonly ThrowingAttendanceService Instance = new();

        public Task<ApiResponse<AttendanceStatusResponse>> StatusAsync(Guid actorUserId) => throw new InvalidOperationException("service must not be reached");
        public Task<ApiResponse<AttendanceStatusResponse>> ClockInAsync(Guid actorUserId, AttendanceClockRequest request, string? idempotencyKey = null) => throw new InvalidOperationException("service must not be reached");
        public Task<ApiResponse<AttendanceStatusResponse>> ClockOutAsync(Guid actorUserId, AttendanceClockRequest request, string? idempotencyKey = null) => throw new InvalidOperationException("service must not be reached");
        public Task<ApiResponse<PaginatedResponse<AttendanceHistoryItem>>> HistoryAsync(Guid actorUserId, AttendanceHistoryQuery query) => throw new InvalidOperationException("service must not be reached");
    }

    // ── §9.6 error-code → HTTP mapping (controller switch) ────

    [Fact]
    public void Controller_ErrorSwitch_ProducesTheSection96Statuses()
    {
        var codes = new (string Code, int Status)[]
        {
            ("INVALID_QR", 400), ("QR_REVOKED", 400), ("QR_WRONG_ORGANIZATION", 400), ("LOCATION_INACTIVE", 400),
            ("UNAUTHORIZED", 401),
            ("FORBIDDEN", 403), ("NOT_LINKED", 403), ("FORBIDDEN_SCOPE", 403), ("MODULE_DISABLED", 403),
            ("NOT_FOUND", 404), ("STAFF_NOT_FOUND", 404),
            ("ALREADY_CLOCKED_IN", 409), ("NOT_CLOCKED_IN", 409), ("ATTENDANCE_DISABLED", 409),
            ("ATTENDANCE_NOT_CONFIGURED", 409), ("VERIFICATION_METHOD_UNAVAILABLE", 409),
            ("IDEMPOTENCY_CONFLICT", 409),
        };

        foreach (var (code, status) in codes)
        {
            var service = new StubAttendanceService(ApiResponse<AttendanceStatusResponse>.Fail(code, "m"));
            var controller = new AttendanceController(service);
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
            controller.ControllerContext.HttpContext.User = ClaimsWithUserId(Guid.NewGuid());

            var action = controller.ClockIn(new AttendanceClockRequest { Token = "punched:attendance:v1:x" }).Result;

            Assert.Equal(status, action switch
            {
                ObjectResult o => o.StatusCode ?? 0,
                StatusCodeResult s => s.StatusCode,
                _ => 0,
            });
        }
    }

    [Fact]
    public void Controller_Success_Returns200()
    {
        var service = new StubAttendanceService(ApiResponse<AttendanceStatusResponse>.Ok(new AttendanceStatusResponse
        {
            State = "clocked_in",
        }));
        var controller = new AttendanceController(service);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        controller.ControllerContext.HttpContext.User = ClaimsWithUserId(Guid.NewGuid());

        var action = controller.ClockIn(new AttendanceClockRequest { Token = "punched:attendance:v1:x" }).Result;
        var ok = Assert.IsType<OkObjectResult>(action);
        Assert.True(((ApiResponse<AttendanceStatusResponse>)ok.Value!).Success);
    }

    private static ClaimsPrincipal ClaimsWithUserId(Guid userId)
    {
        var identity = new ClaimsIdentity(authenticationType: "test");
        identity.AddClaim(new Claim("userId", userId.ToString()));
        identity.AddClaim(new Claim(ClaimTypes.Role, "Staff"));
        return new ClaimsPrincipal(identity);
    }

    private sealed class StubAttendanceService(ApiResponse<AttendanceStatusResponse> response) : IAttendanceService
    {
        public Task<ApiResponse<AttendanceStatusResponse>> StatusAsync(Guid actorUserId) => Task.FromResult(response);
        public Task<ApiResponse<AttendanceStatusResponse>> ClockInAsync(Guid actorUserId, AttendanceClockRequest request, string? idempotencyKey = null) => Task.FromResult(response);
        public Task<ApiResponse<AttendanceStatusResponse>> ClockOutAsync(Guid actorUserId, AttendanceClockRequest request, string? idempotencyKey = null) => Task.FromResult(response);
        public Task<ApiResponse<PaginatedResponse<AttendanceHistoryItem>>> HistoryAsync(Guid actorUserId, AttendanceHistoryQuery query) => throw new InvalidOperationException();
    }
}