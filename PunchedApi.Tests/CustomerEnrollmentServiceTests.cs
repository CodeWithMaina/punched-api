using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;
using PunchedApi.Infrastructure.Repositories;

namespace PunchedApi.Tests;

/// <summary>Customer enrollment: membership, booking gate, history preservation.</summary>
public class CustomerEnrollmentServiceTests
{
    private static (ApplicationDbContext ctx, CustomerEnrollmentService svc, AppointmentService appts)
        CreateEnv(out User customer, out Business business)
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(conn).Options;
        var ctx = new ApplicationDbContext(options);
        ctx.Database.EnsureCreated();
        var owner = new User { Id = Guid.NewGuid(), Email = "o@t.com", FullName = "O", Role = UserRole.Business, CreatedAt = DateTime.UtcNow,
            Auth = new UserAuth { Id = Guid.NewGuid(), Email = "o@t.com", PasswordHash = "x", IsVerified = true, CreatedAt = DateTime.UtcNow } };
        customer = new User { Id = Guid.NewGuid(), Email = "c@t.com", FullName = "C", Role = UserRole.Customer, CreatedAt = DateTime.UtcNow,
            Auth = new UserAuth { Id = Guid.NewGuid(), Email = "c@t.com", PasswordHash = "x", IsVerified = true, CreatedAt = DateTime.UtcNow } };
        business = new Business { Id = Guid.NewGuid(), Name = "B", Category = "salon", Location = "Nairobi", MpesaNumber = "1", OwnerId = owner.Id, TimeZoneId = "UTC", CreatedAt = DateTime.UtcNow };
        var staff = new User { Id = Guid.NewGuid(), Email = "s@t.com", FullName = "S", Role = UserRole.Staff, StaffBusinessId = business.Id, CreatedAt = DateTime.UtcNow,
            Auth = new UserAuth { Id = Guid.NewGuid(), Email = "s@t.com", PasswordHash = "x", IsVerified = true, CreatedAt = DateTime.UtcNow } };
        ctx.Users.AddRange(owner, customer, staff);
        ctx.Businesses.Add(business);
        ctx.SaveChanges();
        _staff = staff;
        var uow = new UnitOfWork(ctx);
        var svc = new CustomerEnrollmentService(uow, ctx, TestHelpers.CreateLogger<CustomerEnrollmentService>());
        return (ctx, svc, BookingTestBase.CreateAppointmentService(ctx));
    }

    private static User _staff = null!;

    private static async Task<ServiceCatalogItem> SeedBookableServiceAsync(ApplicationDbContext ctx, Business b)
    {
        var s = new ServiceCatalogItem { Id = Guid.NewGuid(), BusinessId = b.Id, Name = "Cut", DurationMinutes = 30, Price = 100, IsActive = true, CreatedAt = DateTime.UtcNow };
        var slot = DateTime.UtcNow.Date.AddDays(3);
        await ctx.AddRangeAsync(
            s,
            new StaffServiceAssignment { Id = Guid.NewGuid(), BusinessId = b.Id, StaffUserId = _staff.Id, ServiceCatalogItemId = s.Id, CreatedAt = DateTime.UtcNow },
            new StaffShift { Id = Guid.NewGuid(), BusinessId = b.Id, StaffUserId = _staff.Id, Date = DateOnly.FromDateTime(slot), StartHour = 8, EndHour = 18, IsWorking = true, CreatedAt = DateTime.UtcNow });
        await ctx.SaveChangesAsync();
        return s;
    }

    [Fact]
    public async Task Enroll_Persists_And_IsIdempotent()
    {
        var (ctx, svc, _) = CreateEnv(out var c, out var b);
        using (ctx)
        {
            var r1 = await svc.EnrollAsync(c.Id, b.Id, "qr");
            Assert.True(r1.Success);
            var r2 = await svc.EnrollAsync(c.Id, b.Id, "qr");
            Assert.True(r2.Success);
            Assert.Equal(r1.Data!.Id, r2.Data!.Id);
            Assert.Equal(1, await ctx.CustomerBusinessEnrollments.CountAsync());
        }
    }

    [Fact]
    public async Task Booking_Without_Enrollment_Is_Rejected()
    {
        var (ctx, svc, appts) = CreateEnv(out var c, out var b);
        using (ctx)
        {
            var s = await SeedBookableServiceAsync(ctx, b);
            var r = await appts.CreateAppointmentAsync(c.Id, "Customer", new PunchedApi.Application.DTOs.CreateAppointmentRequest
            { BusinessId = b.Id, ServiceIds = new[] { s.Id }, ScheduledAt = DateTime.UtcNow.Date.AddDays(3).AddHours(10) });
            Assert.False(r.Success);
            Assert.Equal("NOT_ENROLLED", r.Error?.Code);
        }
    }

    [Fact]
    public async Task Leave_Preserves_History_And_Blocks_New_Booking()
    {
        var (ctx, svc, appts) = CreateEnv(out var c, out var b);
        using (ctx)
        {
            await svc.EnrollAsync(c.Id, b.Id, "discovery");
            var s = await SeedBookableServiceAsync(ctx, b);
            var slot = DateTime.UtcNow.Date.AddDays(3).AddHours(10);
            var ok = await appts.CreateAppointmentAsync(c.Id, "Customer", new PunchedApi.Application.DTOs.CreateAppointmentRequest
            { BusinessId = b.Id, ServiceIds = new[] { s.Id }, ScheduledAt = slot });
            Assert.True(ok.Success, ok.Error?.Message);
            await svc.LeaveAsync(c.Id, b.Id);
            Assert.False(await svc.IsEnrolledAsync(c.Id, b.Id));
            Assert.Equal(1, await ctx.Appointments.CountAsync());
            var denied = await appts.CreateAppointmentAsync(c.Id, "Customer", new PunchedApi.Application.DTOs.CreateAppointmentRequest
            { BusinessId = b.Id, ServiceIds = new[] { s.Id }, ScheduledAt = slot.AddHours(2) });
            Assert.Equal("NOT_ENROLLED", denied.Error?.Code);
        }
    }

    [Fact]
    public async Task StampCard_Join_Requires_Enrollment_And_Is_Idempotent()
    {
        var (ctx, svc, _) = CreateEnv(out var c, out var b);
        using (ctx)
        {
            var program = new LoyaltyProgram { Id = Guid.NewGuid(), BusinessId = b.Id, Name = "P", StampsRequired = 5, RewardValue = 10, RewardDescription = "R", CreatedAt = DateTime.UtcNow };
            ctx.LoyaltyPrograms.Add(program);
            await ctx.SaveChangesAsync();
            var card = new StampCard { Id = Guid.NewGuid(), ProgramId = program.Id, BusinessId = b.Id, Name = "Card", StampsRequired = 5, RewardDescription = "R", RewardValue = 10, Status = StampCardStatus.Active, CreatedAt = DateTime.UtcNow };
            ctx.StampCards.Add(card);
            await ctx.SaveChangesAsync();
            Assert.Equal("NOT_ENROLLED", (await svc.JoinStampCardAsync(c.Id, card.Id)).Error?.Code);
            await svc.EnrollAsync(c.Id, b.Id, "qr");
            var j1 = await svc.JoinStampCardAsync(c.Id, card.Id);
            Assert.True(j1.Success);
            var j2 = await svc.JoinStampCardAsync(c.Id, card.Id);
            Assert.Equal(j1.Data!.Id, j2.Data!.Id);
            Assert.Single((await svc.GetMyStampCardsAsync(c.Id, b.Id)).Data!);
            await svc.LeaveStampCardAsync(c.Id, card.Id);
            Assert.Empty((await svc.GetMyStampCardsAsync(c.Id)).Data!);
            Assert.Equal(1, await ctx.CustomerStampCards.CountAsync());
        }
    }

    [Fact]
    public async Task Enroll_Unknown_Business_Returns_NotFound()
    {
        var (ctx, svc, _) = CreateEnv(out var c, out _);
        using (ctx)
        {
            var r = await svc.EnrollAsync(c.Id, Guid.NewGuid(), "qr");
            Assert.Equal("NOT_FOUND", r.Error?.Code);
        }
    }
}
