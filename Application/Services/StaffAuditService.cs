using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

/// <summary>
/// Append-only staff lifecycle audit writer/reader.
///
/// <para><b>Same unit of work.</b> <see cref="Record"/> only adds the row to the
/// caller's context; the caller's existing <c>SaveChanges</c> commits the audit
/// event together with the change it describes. A lifecycle change can therefore
/// never commit without its trail, and a rolled-back change leaves no event.</para>
///
/// <para><b>Read-only surface.</b> There is no update/delete method and the API
/// exposes only a list endpoint — the trail cannot be rewritten through ordinary
/// application APIs.</para>
/// </summary>
public interface IStaffAuditService
{
    /// <summary>Stages an event on the current context (caller saves).</summary>
    void Record(
        Guid businessId,
        Guid? staffUserId,
        Guid? actorUserId,
        string eventType,
        string summary,
        object? details = null);

    /// <summary>Reads the business's trail, newest first; optionally one member's events.</summary>
    Task<ApiResponse<List<StaffAuditEventResponse>>> ListAsync(
        Guid callerUserId, Guid? staffUserId, int take, CancellationToken cancellationToken = default);
}

public sealed class StaffAuditService : IStaffAuditService
{
    private const int MaxTake = 200;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ApplicationDbContext _context;
    private readonly IBusinessScopeResolver _scopeResolver;

    public StaffAuditService(ApplicationDbContext context, IBusinessScopeResolver scopeResolver)
    {
        _context = context;
        _scopeResolver = scopeResolver;
    }

    /// <inheritdoc />
    public void Record(
        Guid businessId,
        Guid? staffUserId,
        Guid? actorUserId,
        string eventType,
        string summary,
        object? details = null)
    {
        if (businessId == Guid.Empty || string.IsNullOrWhiteSpace(eventType)) return;

        _context.StaffAuditEvents.Add(new StaffAuditEvent
        {
            Id = Guid.NewGuid(),
            BusinessId = businessId,
            StaffUserId = staffUserId,
            ActorUserId = actorUserId,
            EventType = eventType,
            Summary = Truncate(summary, 300) ?? eventType,
            DetailsJson = details == null ? null : Truncate(JsonSerializer.Serialize(details, JsonOptions), 4000),
            CreatedAt = DateTime.UtcNow
        });
    }

    /// <inheritdoc />
    public async Task<ApiResponse<List<StaffAuditEventResponse>>> ListAsync(
        Guid callerUserId, Guid? staffUserId, int take, CancellationToken cancellationToken = default)
    {
        var businessId = await _scopeResolver.GetOwnedBusinessIdAsync(callerUserId)
            ?? await _scopeResolver.GetMemberBusinessIdAsync(callerUserId);

        if (businessId == null)
            return ApiResponse<List<StaffAuditEventResponse>>.Fail(
                "NOT_FOUND", "No business found for this account.");

        var limit = Math.Clamp(take, 1, MaxTake);

        // A staff id from another tenant simply has no rows to return; the business
        // filter is applied first, so the endpoint cannot be used to probe other tenants.
        var query = _context.StaffAuditEvents.AsNoTracking()
            .Where(e => e.BusinessId == businessId.Value);

        if (staffUserId is Guid staffId)
            query = query.Where(e => e.StaffUserId == staffId);

        var rows = await query
            .OrderByDescending(e => e.CreatedAt)
            .ThenByDescending(e => e.Id)
            .Take(limit)
            .Select(e => new
            {
                e.Id,
                e.BusinessId,
                e.StaffUserId,
                e.ActorUserId,
                e.EventType,
                e.Summary,
                e.DetailsJson,
                e.CreatedAt
            })
            .ToListAsync(cancellationToken);

        var userIds = rows
            .Select(r => r.StaffUserId)
            .Concat(rows.Select(r => r.ActorUserId))
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();

        var names = userIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await _context.Users.AsNoTracking()
                .Where(u => userIds.Contains(u.Id))
                .Select(u => new { u.Id, u.FullName })
                .ToDictionaryAsync(u => u.Id, u => u.FullName, cancellationToken);

        return ApiResponse<List<StaffAuditEventResponse>>.Ok(rows.Select(r => new StaffAuditEventResponse
        {
            Id = r.Id,
            BusinessId = r.BusinessId,
            StaffUserId = r.StaffUserId,
            StaffName = r.StaffUserId.HasValue && names.TryGetValue(r.StaffUserId.Value, out var staffName)
                ? staffName
                : null,
            ActorUserId = r.ActorUserId,
            ActorName = r.ActorUserId.HasValue && names.TryGetValue(r.ActorUserId.Value, out var actorName)
                ? actorName
                : null,
            EventType = r.EventType,
            Summary = r.Summary,
            DetailsJson = r.DetailsJson,
            CreatedAt = r.CreatedAt
        }).ToList());
    }

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];
}
