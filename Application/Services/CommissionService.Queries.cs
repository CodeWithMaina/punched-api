using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Application.Services;

/// <summary>
/// Read-side commission management: ledger history with DB-first filtering,
/// sorting and pagination.
///
/// <para><b>Scoping.</b> Every query is anchored on the caller own business. A
/// staff caller may only ever see their OWN entries - the staff filter is forced
/// server-side and can never be widened by a request field. Customers have no
/// commission surface at all.</para>
/// </summary>
public partial class CommissionService
{
    /// <inheritdoc />
    public async Task<ApiResponse<PaginatedResponse<CommissionEntryResponse>>> GetEntriesAsync(
        Guid callerUserId, string role, CommissionEntriesQueryRequest request)
    {
        var businessId = await ResolveCallerBusinessIdAsync(callerUserId, role);
        if (businessId == null)
            return ApiResponse<PaginatedResponse<CommissionEntryResponse>>.Fail("NOT_FOUND", "No business found for this account.");

        if (!await HasPermissionAsync(callerUserId, businessId.Value, "commissions.view"))
            return ApiResponse<PaginatedResponse<CommissionEntryResponse>>.Fail("FORBIDDEN", "You are not allowed to view commissions.");

        var page = Math.Max(request.Page, 1);
        var pageSize = Math.Clamp(request.PageSize, 1, 200);

        var query = _context.CommissionEntries.AsNoTracking()
            .Where(e => e.BusinessId == businessId.Value);

        // A staff caller is hard-scoped to their own ledger; the client cannot
        // widen it by naming somebody else.
        if (IsRole(role, "Staff"))
            query = query.Where(e => e.StaffUserId == callerUserId);
        else if (request.StaffUserId.HasValue)
            query = query.Where(e => e.StaffUserId == request.StaffUserId.Value);

        if (request.ServiceId.HasValue) query = query.Where(e => e.ServiceCatalogItemId == request.ServiceId.Value);
        if (request.AppointmentId.HasValue) query = query.Where(e => e.AppointmentId == request.AppointmentId.Value);
        if (request.CustomerId.HasValue) query = query.Where(e => e.CustomerId == request.CustomerId.Value);
        if (request.From.HasValue) query = query.Where(e => e.CreatedAt >= request.From.Value);
        if (request.To.HasValue) query = query.Where(e => e.CreatedAt <= request.To.Value);

        if (!string.IsNullOrWhiteSpace(request.Status))
        {
            var statuses = ParseStatuses(request.Status);
            if (statuses.Count == 0)
                return ApiResponse<PaginatedResponse<CommissionEntryResponse>>.Fail("VALIDATION_ERROR", "Unknown commission status filter.");
            query = query.Where(e => statuses.Contains(e.Status));
        }

        if (!string.IsNullOrWhiteSpace(request.EntryType))
        {
            var types = ParseEntryTypes(request.EntryType);
            if (types.Count == 0)
                return ApiResponse<PaginatedResponse<CommissionEntryResponse>>.Fail("VALIDATION_ERROR", "Unknown commission entryType filter.");
            query = query.Where(e => types.Contains(e.EntryType));
        }

        var total = await query.CountAsync();
        var entries = await OrderEntries(query, request.SortBy, request.SortDirection)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var responses = await MapEntriesAsync(entries);

        return ApiResponse<PaginatedResponse<CommissionEntryResponse>>.Ok(new PaginatedResponse<CommissionEntryResponse>
        {
            Items = responses,
            TotalCount = total,
            Page = page,
            PageSize = pageSize
        });
    }
}

/// <summary>Entry ordering, filter parsing and response mapping.</summary>
public partial class CommissionService
{
    private static IQueryable<CommissionEntry> OrderEntries(
        IQueryable<CommissionEntry> query, string? sortBy, string? sortDirection)
    {
        var descending = !string.Equals(sortDirection?.Trim(), "asc", StringComparison.OrdinalIgnoreCase);

        return (sortBy?.Trim().ToLowerInvariant()) switch
        {
            "amount" => descending ? query.OrderByDescending(e => e.Amount) : query.OrderBy(e => e.Amount),
            "status" => descending ? query.OrderByDescending(e => e.Status) : query.OrderBy(e => e.Status),
            "baseamount" => descending ? query.OrderByDescending(e => e.BaseAmount) : query.OrderBy(e => e.BaseAmount),
            "createdat" => descending ? query.OrderByDescending(e => e.CreatedAt) : query.OrderBy(e => e.CreatedAt),
            _ => query.OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id)
        };
    }

    private static HashSet<CommissionEntryStatus> ParseStatuses(string value)
    {
        var result = new HashSet<CommissionEntryStatus>();
        foreach (var raw in value.Split(",", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse<CommissionEntryStatus>(raw, ignoreCase: true, out var parsed))
                result.Add(parsed);
        }

        return result;
    }

    private static HashSet<CommissionEntryType> ParseEntryTypes(string value)
    {
        var result = new HashSet<CommissionEntryType>();
        foreach (var raw in value.Split(",", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse<CommissionEntryType>(raw, ignoreCase: true, out var parsed))
                result.Add(parsed);
        }

        return result;
    }

    private async Task<List<CommissionEntryResponse>> MapEntriesAsync(List<CommissionEntry> entries)
    {
        if (entries.Count == 0) return new List<CommissionEntryResponse>();

        var staffIds = entries.Select(e => e.StaffUserId).Distinct().ToList();
        var customerIds = entries.Where(e => e.CustomerId.HasValue).Select(e => e.CustomerId!.Value).Distinct().ToList();
        var serviceIds = entries.Where(e => e.ServiceCatalogItemId.HasValue).Select(e => e.ServiceCatalogItemId!.Value).Distinct().ToList();

        var staffNames = await _context.Users.AsNoTracking()
            .Where(u => staffIds.Contains(u.Id))
            .Select(u => new { u.Id, u.FullName })
            .ToDictionaryAsync(u => u.Id, u => u.FullName);

        var customerNames = await _context.Users.AsNoTracking()
            .Where(u => customerIds.Contains(u.Id))
            .Select(u => new { u.Id, u.FullName })
            .ToDictionaryAsync(u => u.Id, u => u.FullName);

        var serviceNames = await _context.ServiceCatalogItems.AsNoTracking()
            .Where(s => serviceIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Name })
            .ToDictionaryAsync(s => s.Id, s => s.Name);

        return entries.Select(e =>
        {
            var response = MapEntry(e);
            response.StaffName = staffNames.TryGetValue(e.StaffUserId, out var sn) ? sn : null;
            response.CustomerName = e.CustomerId.HasValue && customerNames.TryGetValue(e.CustomerId.Value, out var cn) ? cn : null;
            response.ServiceName = e.ServiceCatalogItemId.HasValue && serviceNames.TryGetValue(e.ServiceCatalogItemId.Value, out var vn) ? vn : null;
            return response;
        }).ToList();
    }

    private static CommissionEntryResponse MapEntry(CommissionEntry entry) => new()
    {
        Id = entry.Id,
        BusinessId = entry.BusinessId,
        StaffUserId = entry.StaffUserId,
        CustomerId = entry.CustomerId,
        AppointmentId = entry.AppointmentId,
        AppointmentResourceId = entry.AppointmentResourceId,
        ServiceId = entry.ServiceCatalogItemId,
        PaymentId = entry.PaymentId,
        PayoutId = entry.PayoutId,
        EntryType = Wire(entry.EntryType.ToString()),
        SourceType = Wire(entry.SourceType.ToString()),
        Status = Wire(entry.Status.ToString()),
        ConfigScope = Wire(entry.ConfigScope.ToString()),
        ConfigId = entry.ConfigId,
        ConfigSnapshotJson = entry.ConfigSnapshotJson,
        CalculationType = Wire(entry.CalculationType.ToString()),
        RatePercent = entry.RatePercent,
        FixedAmount = entry.FixedAmount,
        BaseAmount = entry.BaseAmount,
        Amount = entry.Amount,
        Currency = entry.Currency,
        Reason = entry.Reason,
        AdjustmentOfEntryId = entry.AdjustmentOfEntryId,
        ReversesEntryId = entry.ReversesEntryId,
        ReversedByEntryId = entry.ReversedByEntryId,
        DedupeKey = entry.DedupeKey,
        CreatedAt = entry.CreatedAt,
        UpdatedAt = entry.UpdatedAt,
        ApprovedAt = entry.ApprovedAt,
        ApprovedByUserId = entry.ApprovedByUserId,
        PayableAt = entry.PayableAt,
        PaidAt = entry.PaidAt,
        DisputedAt = entry.DisputedAt,
        DisputeReason = entry.DisputeReason,
        ReversedAt = entry.ReversedAt,
        VoidedAt = entry.VoidedAt,
        VoidReason = entry.VoidReason
    };
}
