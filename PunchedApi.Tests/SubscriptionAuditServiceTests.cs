using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PunchedApi.Application.Services;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Tests;

/// <summary>
/// Unit tests for SubscriptionAuditService: correct actor/target/payload/reason
/// persistence, and never throwing even when the underlying write fails.
/// </summary>
public class SubscriptionAuditServiceTests : IDisposable
{
    private readonly ApplicationDbContext _db;
    private readonly SubscriptionAuditService _service;

    public SubscriptionAuditServiceTests()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _db = new ApplicationDbContext(options);
        _service = new SubscriptionAuditService(_db, TestHelpers.CreateLogger<SubscriptionAuditService>());
    }

    [Fact]
    public async Task Record_PersistsActorTargetsPayloadAndReason()
    {
        var actor = Guid.NewGuid();
        var businessId = Guid.NewGuid();
        var planId = Guid.NewGuid();

        await _service.RecordAsync("TIER_PUBLISHED", actor,
            targetBusinessId: businessId, targetPlanId: planId,
            payloadJson: "{\"key\":\"pro\"}", reason: "published");

        var row = await _db.SubscriptionAuditLogs.AsNoTracking().SingleAsync();
        Assert.Equal("TIER_PUBLISHED", row.Action);
        Assert.Equal(actor, row.ActorUserId);
        Assert.Equal(businessId, row.TargetBusinessId);
        Assert.Equal(planId, row.TargetPlanId);
        Assert.Equal("{\"key\":\"pro\"}", row.PayloadJson);
        Assert.Equal("published", row.Reason);
    }

    [Fact]
    public async Task Record_WithNullOptionals_PersistsNulls()
    {
        await _service.RecordAsync("TIER_CREATED", null,
            targetBusinessId: null, targetPlanId: null, payloadJson: null, reason: null);
        var row = await _db.SubscriptionAuditLogs.AsNoTracking().SingleAsync();
        Assert.Null(row.ActorUserId);
        Assert.Null(row.TargetBusinessId);
        Assert.Null(row.TargetPlanId);
        Assert.Null(row.PayloadJson);
        Assert.Null(row.Reason);
    }

    [Fact]
    public void Record_DoesNotThrow_WhenWriteFails()
    {
        // Disposing the context forces the internal write to fail; the service
        // must swallow the exception (audit failures never break the primary mutation).
        _db.Dispose();
        var ex = Record.ExceptionAsync(() => _service.RecordAsync(
            "TIER_UPDATED", Guid.NewGuid(), targetBusinessId: null, targetPlanId: null,
            payloadJson: "{}", reason: "r")).GetAwaiter().GetResult();
        Assert.Null(ex);
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch { /* already disposed in the failure test */ }
    }
}
