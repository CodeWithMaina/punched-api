using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PunchedApi.API.Controllers;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Notifications;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Tests.Notifications;

/// <summary>
/// Regression tests for template-override input validation: unknown types and
/// channels must fail as 4xx-mapped validation errors (not 500s), (type,
/// channel) collisions must be reported, and repeated edits must not insert
/// duplicate <c>(override_id, version)</c> history rows.
/// </summary>
public class TemplateOverrideValidationTests
{
    // ══════════════════════════════════════════════════════════════════
    //  Create validation
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task CreateOverride_rejects_an_unknown_type_as_a_validation_error()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var service = CreateService(context);
        var businessId = await SeedBusinessAsync(context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateOverrideAsync(businessId, Request(type: "not.a.real.type")));

        // The controller maps InvalidOperationException to 400; a
        // KeyNotFoundException from NotificationTypes.Get would have been a 500.
        Assert.Contains("Unknown notification type", ex.Message);
    }

    [Fact]
    public async Task CreateOverride_rejects_an_unsupported_channel()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var service = CreateService(context);
        var businessId = await SeedBusinessAsync(context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateOverrideAsync(businessId, Request(channel: "carrier_pigeon")));

        Assert.Contains("Unsupported channel", ex.Message);
        Assert.Empty(await context.NotificationTemplateOverrides.ToListAsync());
    }

    [Fact]
    public async Task CreateOverride_normalises_channel_case_and_rejects_the_duplicate_pair()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var service = CreateService(context);
        var businessId = await SeedBusinessAsync(context);

        var created = await service.CreateOverrideAsync(businessId, Request(channel: "EMAIL"));

        Assert.Equal("email", created.Channel);
        Assert.Equal(1, created.Version);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateOverrideAsync(businessId, Request(channel: "Email")));
        Assert.Contains("already exists", ex.Message);
    }

    // ══════════════════════════════════════════════════════════════════
    //  Update / history
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task UpdateOverride_twice_keeps_one_history_row_per_version()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var service = CreateService(context);
        var businessId = await SeedBusinessAsync(context);

        var created = await service.CreateOverrideAsync(businessId, Request(body: "one"));
        var second = await service.UpdateOverrideAsync(
            businessId, created.Id, Request(body: "two", updatedBy: "owner-2"));
        var third = await service.UpdateOverrideAsync(
            businessId, created.Id, Request(body: "three", updatedBy: "owner-3"));

        Assert.Equal(2, second.Version);
        Assert.Equal(3, third.Version);

        var versions = await service.ListVersionsAsync(businessId, created.Id);
        Assert.Equal(new[] { 1, 2, 3 }, versions.Select(v => v.Version).OrderBy(v => v).ToArray());
        Assert.Equal(3, await context.NotificationTemplateOverrideVersions.CountAsync());
    }

    [Fact]
    public async Task UpdateOverride_cannot_move_onto_an_occupied_type_channel_pair()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var service = CreateService(context);
        var businessId = await SeedBusinessAsync(context);

        var email = await service.CreateOverrideAsync(businessId, Request(channel: "email"));
        var sms = await service.CreateOverrideAsync(businessId, Request(channel: "sms"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateOverrideAsync(businessId, sms.Id, Request(channel: "email")));

        Assert.Contains("already exists", ex.Message);
        var unchanged = await service.GetOverrideAsync(businessId, email.Id);
        Assert.Equal(1, unchanged!.Version);
    }

    [Fact]
    public async Task RestoreVersion_appends_a_new_version_without_duplicating_history()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var service = CreateService(context);
        var businessId = await SeedBusinessAsync(context);

        var created = await service.CreateOverrideAsync(businessId, Request(body: "original"));
        await service.UpdateOverrideAsync(businessId, created.Id, Request(body: "changed"));

        var restored = await service.RestoreVersionAsync(
            businessId, created.Id, version: 1, updatedBy: "owner");

        Assert.Equal(3, restored.Version);
        Assert.Equal("original", restored.BodyTemplateText);
        var versions = await service.ListVersionsAsync(businessId, created.Id);
        Assert.Equal(new[] { 1, 2, 3 }, versions.Select(v => v.Version).OrderBy(v => v).ToArray());
    }

    // ══════════════════════════════════════════════════════════════════
    //  Read paths
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetOverride_is_tenant_scoped()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var service = CreateService(context);
        var businessId = await SeedBusinessAsync(context);
        var otherBusinessId = await SeedBusinessAsync(context);

        var created = await service.CreateOverrideAsync(businessId, Request());

        Assert.NotNull(await service.GetOverrideAsync(businessId, created.Id));
        Assert.Null(await service.GetOverrideAsync(otherBusinessId, created.Id));
    }

    [Fact]
    public async Task Resolver_reports_the_override_id_and_version_it_used()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var service = CreateService(context);
        var businessId = await SeedBusinessAsync(context);
        var created = await service.CreateOverrideAsync(businessId, Request(body: "overridden"));

        var resolver = new TemplateOverrideResolver(
            context, TestHelpers.CreateLogger<TemplateOverrideResolver>());
        var effective = await resolver.ResolveAsync(businessId, "appointment.booked", "email");

        Assert.NotNull(effective);
        Assert.Equal(EffectiveTemplateSource.Override, effective!.Source);
        Assert.Equal(created.Id, effective.OverrideId);
        Assert.Equal(1, effective.OverrideVersion);
        Assert.Equal("overridden", effective.Body);
    }

    // ══════════════════════════════════════════════════════════════════
    //  Controller-level normalisation (list / effective)
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ListOverrides_trims_the_type_and_lowercases_the_channel()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var service = CreateService(context);
        var businessId = await SeedBusinessAsync(context);
        await service.CreateOverrideAsync(businessId, Request(channel: "EMAIL"));
        var controller = CreateController(service);

        // " EMAIL " is a valid channel once normalised; comparing it verbatim
        // against the canonical list used to fail the request with a 400.
        var result = await controller.ListOverrides(businessId, " appointment.booked ", " EMAIL ");

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<ApiResponse<PaginatedNotificationTemplateOverridesDto>>(ok.Value);
        Assert.True(payload.Success);
        Assert.NotNull(payload.Data);
        Assert.Equal(1, payload.Data!.Total);
        Assert.Equal("email", payload.Data.Items.Single().Channel);
    }

    [Fact]
    public async Task ListOverrides_rejects_an_unknown_channel_after_normalisation()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var service = CreateService(context);
        var businessId = await SeedBusinessAsync(context);
        var controller = CreateController(service);

        var result = await controller.ListOverrides(businessId, null, "CARRIER_PIGEON");

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        var payload = Assert.IsType<ApiResponse<PaginatedNotificationTemplateOverridesDto>>(bad.Value);
        Assert.False(payload.Success);
        Assert.Contains("not supported", payload.Error!.Message);
    }

    [Fact]
    public async Task GetEffective_accepts_a_mixed_case_channel()
    {
        using var connection = BookingTestBase.CreateConnection();
        using var context = BookingTestBase.CreateContext(connection);
        var service = CreateService(context);
        var businessId = await SeedBusinessAsync(context);
        var controller = CreateController(service);

        var result = await controller.GetEffective(businessId, " appointment.booked ", " Email ");

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<ApiResponse<EffectiveTemplateDto>>(ok.Value);
        Assert.True(payload.Success);
        Assert.Equal("email", payload.Data!.Channel);
    }

    // ══════════════════════════════════════════════════════════════════
    //  Helpers
    // ══════════════════════════════════════════════════════════════════

    private static TemplateOverrideService CreateService(ApplicationDbContext context)
        => new(
            context,
            new TemplateRenderer(TestHelpers.CreateLogger<TemplateRenderer>()),
            new TemplateOverrideResolver(
                context, TestHelpers.CreateLogger<TemplateOverrideResolver>()),
            TestHelpers.CreateLogger<TemplateOverrideService>());

    private static TemplateOverridesController CreateController(TemplateOverrideService service)
    {
        // Platform Admin bypasses tenant-ownership resolution, so the scope
        // resolver is never reached here; the stub keeps the ctor honest.
        var controller = new TemplateOverridesController(
            service,
            new AdminScopeResolverStub(),
            TestHelpers.CreateLogger<TemplateOverridesController>());

        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Role, "Admin"), new Claim("userId", Guid.NewGuid().ToString()) },
            authenticationType: "Test");

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return controller;
    }

    private sealed class AdminScopeResolverStub : IBusinessScopeResolver
    {
        public Task<Guid?> GetOwnedBusinessIdAsync(Guid ownerId) => Task.FromResult<Guid?>(null);
        public void InvalidateOwner(Guid ownerId) { }
    }

    private static async Task<Guid> SeedBusinessAsync(ApplicationDbContext context)
    {
        var owner = BookingTestBase.CreateOwner($"owner-{Guid.NewGuid():N}@test.com");
        context.Users.Add(owner);
        var business = BookingTestBase.CreateBusiness(owner.Id, "Template Salon");
        context.Businesses.Add(business);
        await context.SaveChangesAsync();
        return business.Id;
    }

    private static UpsertNotificationTemplateOverrideRequest Request(
        string type = "appointment.booked",
        string channel = "email",
        string body = "hello",
        string updatedBy = "owner")
        => new()
        {
            Type = type,
            Channel = channel,
            SubjectTemplate = null,
            BodyTemplateHtml = $"<p>{body}</p>",
            BodyTemplateText = body,
            UpdatedBy = updatedBy,
        };
}
