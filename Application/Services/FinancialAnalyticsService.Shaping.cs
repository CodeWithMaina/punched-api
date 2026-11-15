using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Application.Services;

/// <summary>
/// Attribution and shaping: turning verified income into "which service, which category, which person
/// earned it", and turning dated amounts into a trend a chart can draw.
/// </summary>
public partial class FinancialAnalyticsService
{
    /// <summary>
    /// Attributes appointment income to the services that produced it.
    ///
    /// <para><b>How the split works.</b> An appointment's income is distributed across its service
    /// lines in proportion to their prices, so a partially paid visit attributes honestly instead of
    /// dumping the whole amount on the first line. Walk-in income has no service line and therefore
    /// takes no part — inventing an attribution for it would be a lie with a chart on it.</para>
    /// </summary>
    private async Task<(List<FinancialBreakdownItem> ByService, List<FinancialBreakdownItem> ByCategory,
        List<FinancialBreakdownItem> ByStaff)> AttributeToServicesAsync(
        Guid businessId, IReadOnlyList<RevenueRow> incomeRows)
    {
        var empty = new List<FinancialBreakdownItem>();

        var appointmentIds = incomeRows
            .Where(r => r.AppointmentId.HasValue)
            .Select(r => r.AppointmentId!.Value)
            .Distinct()
            .ToList();

        if (appointmentIds.Count == 0) return (empty, empty, empty);

        var lines = await _context.AppointmentResources.AsNoTracking()
            .Where(r => appointmentIds.Contains(r.AppointmentId))
            .Select(r => new ServiceLineDetailRow(r.AppointmentId, r.ServiceCatalogItemId, r.Name, r.Price))
            .ToListAsync();

        if (lines.Count == 0) return (empty, empty, empty);

        var serviceIds = lines.Select(l => l.ServiceCatalogItemId).Distinct().ToList();

        var categoryByService = await _context.ServiceCatalogItems.AsNoTracking()
            .Where(s => serviceIds.Contains(s.Id) && s.CategoryId != null)
            .Select(s => new ServiceCategoryRefRow(s.Id, s.CategoryId!.Value))
            .ToDictionaryAsync(s => s.Id, s => s.CategoryId);

        var categoryNames = await _context.ServiceCategories.AsNoTracking()
            .Where(c => c.BusinessId == businessId)
            .Select(c => new NamedRow(c.Id, c.Name))
            .ToDictionaryAsync(c => c.Id, c => c.Name);

        var staffByAppointment = await _context.Appointments.AsNoTracking()
            .Where(a => appointmentIds.Contains(a.Id) && a.StaffUserId != null)
            .Select(a => new AppointmentStaffRefRow(a.Id, a.StaffUserId!.Value))
            .ToDictionaryAsync(a => a.Id, a => a.StaffUserId);

        var staffIds = staffByAppointment.Values.Distinct().ToList();

        var staffNames = await _context.Users.AsNoTracking()
            .Where(u => staffIds.Contains(u.Id))
            .Select(u => new UserIdNameRow(u.Id, u.FullName))
            .ToDictionaryAsync(u => u.Id, u => u.FullName);

        var linesByAppointment = lines.GroupBy(l => l.AppointmentId).ToDictionary(g => g.Key, g => g.ToList());

        var byService = new Dictionary<string, (decimal Amount, int Count)>(StringComparer.Ordinal);
        var byCategory = new Dictionary<Guid, (decimal Amount, int Count)>();
        var byStaff = new Dictionary<Guid, (decimal Amount, int Count)>();

        foreach (var row in incomeRows.Where(r => r.AppointmentId.HasValue))
        {
            if (!linesByAppointment.TryGetValue(row.AppointmentId!.Value, out var appointmentLines)) continue;

            var lineTotal = appointmentLines.Sum(l => l.Price);

            foreach (var line in appointmentLines)
            {
                var share = lineTotal > 0m
                    ? decimal.Round(row.Amount * line.Price / lineTotal, 2, MidpointRounding.AwayFromZero)
                    : decimal.Round(row.Amount / appointmentLines.Count, 2, MidpointRounding.AwayFromZero);

                var service = byService.TryGetValue(line.Name, out var foundService) ? foundService : (0m, 0);
                byService[line.Name] = (service.Item1 + share, service.Item2 + 1);

                if (categoryByService.TryGetValue(line.ServiceCatalogItemId, out var categoryId))
                {
                    var category = byCategory.TryGetValue(categoryId, out var foundCategory)
                        ? foundCategory
                        : (0m, 0);
                    byCategory[categoryId] = (category.Item1 + share, category.Item2 + 1);
                }
            }

            if (staffByAppointment.TryGetValue(row.AppointmentId.Value, out var staffId))
            {
                var staff = byStaff.TryGetValue(staffId, out var foundStaff) ? foundStaff : (0m, 0);
                byStaff[staffId] = (staff.Item1 + row.Amount, staff.Item2 + 1);
            }
        }

        return (
            WeightedBreakdown(byService.Select(p => (p.Value.Amount, p.Key, p.Value.Count)), key => (key, key)),
            WeightedBreakdown(
                byCategory.Select(p => (p.Value.Amount, p.Key, p.Value.Count)),
                id => categoryNames.TryGetValue(id, out var name)
                    ? (id.ToString(), name)
                    : (id.ToString(), "Uncategorised")),
            WeightedBreakdown(
                byStaff.Select(p => (p.Value.Amount, p.Key, p.Value.Count)),
                id => staffNames.TryGetValue(id, out var name)
                    ? (id.ToString(), name)
                    : (id.ToString(), "Unassigned")));
    }

    /// <summary>A breakdown built from pre-aggregated (amount, key, count) triples.</summary>
    private static List<FinancialBreakdownItem> WeightedBreakdown<TKey>(
        IEnumerable<(decimal Amount, TKey Key, int Count)> rows,
        Func<TKey, (string Key, string Label)> describe)
    {
        var groups = new Dictionary<string, (string Label, decimal Amount, int Count)>(StringComparer.Ordinal);

        foreach (var (amount, key, count) in rows)
        {
            var (groupKey, label) = describe(key);
            var current = groups.TryGetValue(groupKey, out var found)
                ? found
                : (Label: label, Amount: 0m, Count: 0);
            groups[groupKey] = (current.Label, current.Amount + amount, current.Count + count);
        }

        var total = groups.Values.Sum(g => g.Amount);

        return groups
            .Select(pair => new FinancialBreakdownItem
            {
                Key = pair.Key,
                Label = pair.Value.Label,
                Amount = pair.Value.Amount,
                Count = pair.Value.Count,
                Percentage = total > 0m
                    ? decimal.Round(pair.Value.Amount * 100m / total, 1, MidpointRounding.AwayFromZero)
                    : 0m
            })
            .OrderByDescending(item => item.Amount)
            .ToList();
    }

    /// <summary>
    /// Buckets dated amounts into the trend series for the window: day buckets for a month, week
    /// buckets for a season, month buckets for a year — the same rule the window advertises. Only
    /// buckets that have rows are emitted; the chart draws gaps rather than inventing zeros.
    /// </summary>
    private static List<FinancialTrendPoint> Trend(
        IEnumerable<(DateTime Date, decimal Amount)> rows, string granularity)
    {
        var buckets = new Dictionary<string, (string Label, decimal Amount, int Count)>(StringComparer.Ordinal);

        foreach (var (date, amount) in rows)
        {
            var (key, label) = Bucket(date, granularity);
            var current = buckets.TryGetValue(key, out var found)
                ? found
                : (Label: label, Amount: 0m, Count: 0);
            buckets[key] = (current.Label, current.Amount + amount, current.Count + 1);
        }

        return buckets
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new FinancialTrendPoint
            {
                Period = pair.Key,
                Label = pair.Value.Label,
                Amount = pair.Value.Amount,
                Count = pair.Value.Count
            })
            .ToList();
    }

    private static (string Key, string Label) Bucket(DateTime date, string granularity) => granularity switch
    {
        "month" => ($"{date:yyyy-MM}", date.ToString("MMM yyyy")),
        "week" => ($"W{WeekStart(date):yyyy-MM-dd}", $"Week of {WeekStart(date):d MMM}"),
        _ => ($"{date:yyyy-MM-dd}", date.ToString("d MMM"))
    };

    /// <summary>Monday-based week start, which is how a Kenyan business week is read.</summary>
    private static DateTime WeekStart(DateTime date) =>
        date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
}
