using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Services;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;
using PunchedApi.Infrastructure.Repositories;

namespace PunchedApi.Tests.Notifications;

/// <summary>
/// The caller's notification inbox, and the two rules that keep it safe.
///
/// <para>An inbox is the one screen where a leak is immediately visible to the wrong person: a
/// staff member reading another staff member's rows, or one business's bookings surfacing inside
/// another business's console. So the ownership predicate and the business check are asserted
/// here rather than assumed.</para>
/// </summary>
public class NotificationInboxTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly NotificationsService _service;

    private readonly Business _businessA;
    private readonly Business _businessB;
    private readonly User _owner;
    private readonly User _staff;

    public NotificationInboxTests()
    {
        _connection = BookingTestBase.CreateConnection();
        _context = BookingTestBase.CreateContext(_connection);

        _owner = BookingTestBase.CreateOwner($"inbox-owner-{Guid.NewGuid():N}@test.com");
        _businessA = BookingTestBase.CreateBusiness(_owner.Id, "Inbox Studio A");

        var otherOwner = BookingTestBase.CreateOwner($"inbox-other-{Guid.NewGuid():N}@test.com");
        _businessB = BookingTestBase.CreateBusiness(otherOwner.Id, "Inbox Studio B");

        _staff = BookingTestBase.CreateStaff(_businessA.Id, $"inbox-staff-{Guid.NewGuid():N}@test.com");

        BookingTestBase.SeedAsync(_context, _owner, otherOwner, _businessA, _businessB, _staff)
            .GetAwaiter().GetResult();

        _service = new NotificationsService(
            new UnitOfWork(_context), _context, TestHelpers.CreateLogger<NotificationsService>());
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private async Task<Notification> Notify(
        Guid userId, Guid? businessId, string type = "appointment.booked", bool isRead = false)
    {
        var row = new Notification
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            BusinessId = businessId,
            Type = type,
            IsRead = isRead,
            PayloadJson = "{\"businessName\":\"Inbox Studio A\"}",
            CreatedAt = DateTime.UtcNow,
        };
        await _context.Notifications.AddAsync(row);
        await _context.SaveChangesAsync();
        return row;
    }

    [Fact]
    public async Task TheInbox_OnlyEverContainsTheCallersOwnRows()
    {
        await Notify(_owner.Id, _businessA.Id);
        await Notify(_staff.Id, _businessA.Id);

        var inbox = await _service.GetInboxAsync(_owner.Id, new NotificationInboxQuery { PageSize = 50 });

        Assert.True(inbox.Success, inbox.Error?.Message);
        Assert.Single(inbox.Data!.Items);
        Assert.Equal(1, inbox.Data.UnreadCount);
    }

    [Fact]
    public async Task ABusinessTheCallerDoesNotBelongTo_IsRefusedRatherThanIgnored()
    {
        await Notify(_owner.Id, _businessA.Id);

        // Dropping the filter would answer a narrower question with a wider list, so the server
        // refuses instead.
        var inbox = await _service.GetInboxAsync(
            _owner.Id, new NotificationInboxQuery { BusinessId = _businessB.Id });

        Assert.False(inbox.Success);
        Assert.Equal("FORBIDDEN", inbox.Error?.Code);
    }

    [Fact]
    public async Task ABusinessScopedRead_StillIncludesPlatformWideRows()
    {
        await Notify(_owner.Id, _businessA.Id);
        await Notify(_owner.Id, null, type: "security.password_changed");

        var inbox = await _service.GetInboxAsync(
            _owner.Id, new NotificationInboxQuery { BusinessId = _businessA.Id, PageSize = 50 });

        Assert.True(inbox.Success, inbox.Error?.Message);
        Assert.Equal(2, inbox.Data!.Items.Count);
    }

    [Fact]
    public async Task PagingAndFilters_ComposeWithoutChangingTheTotal()
    {
        for (var i = 0; i < 5; i++) await Notify(_owner.Id, _businessA.Id);
        await Notify(_owner.Id, _businessA.Id, type: "payment.submitted", isRead: true);

        var firstPage = await _service.GetInboxAsync(
            _owner.Id, new NotificationInboxQuery { Page = 1, PageSize = 2 });

        Assert.True(firstPage.Success, firstPage.Error?.Message);
        Assert.Equal(2, firstPage.Data!.Items.Count);
        Assert.Equal(6, firstPage.Data.TotalCount);
        Assert.Equal(3, firstPage.Data.TotalPages);

        // "Unread" narrows the list but not the badge: six rows exist, five are unread.
        var unread = await _service.GetInboxAsync(
            _owner.Id, new NotificationInboxQuery { Status = "unread", PageSize = 50 });
        Assert.Equal(5, unread.Data!.TotalCount);
        Assert.Equal(5, unread.Data.UnreadCount);

        var byType = await _service.GetInboxAsync(
            _owner.Id, new NotificationInboxQuery { Type = "payment.submitted" });
        Assert.Equal(1, byType.Data!.TotalCount);
    }

    [Fact]
    public async Task MarkingEverythingRead_ClearsTheBadgeAndLeavesArchivedRowsAlone()
    {
        await Notify(_owner.Id, _businessA.Id);
        await Notify(_owner.Id, _businessA.Id);
        var archived = await Notify(_owner.Id, _businessA.Id);
        archived.ArchivedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        var marked = await _service.MarkAllReadAsync(_owner.Id);

        // The archived row was already out of the inbox, so "mark all read" never counted it.
        Assert.Equal(2, marked);
        Assert.Equal(0, await _service.GetUnreadCountAsync(_owner.Id));
    }

    [Fact]
    public async Task ARowIsRenderedWithItsOwnTitleAndTheResourceItOpens()
    {
        var paymentId = Guid.NewGuid();
        var row = new Notification
        {
            Id = Guid.NewGuid(),
            UserId = _owner.Id,
            BusinessId = _businessA.Id,
            Type = "payment.submitted",
            PayloadJson = $"{{\"businessName\":\"Inbox Studio A\",\"amount\":\"450\",\"paymentId\":\"{paymentId}\"}}",
            CreatedAt = DateTime.UtcNow,
        };
        await _context.Notifications.AddAsync(row);
        await _context.SaveChangesAsync();

        var inbox = await _service.GetInboxAsync(_owner.Id, new NotificationInboxQuery());

        var item = Assert.Single(inbox.Data!.Items);
        // The title and the link are the server's, so every client shows the same thing for the
        // same event — and a payment notification opens the payment, not the appointment it pays.
        Assert.Equal("A payment is waiting for your review", item.Title);
        Assert.Equal("PAYMENT", item.Category);
        Assert.Equal("payment", item.ResourceType);
        Assert.Equal(paymentId, item.ResourceId);
    }
}
