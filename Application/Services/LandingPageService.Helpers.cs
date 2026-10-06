using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Modules;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Application.Services;

public partial class LandingPageService
{
    private static LandingPageConfigDto? ParseConfig(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<LandingPageConfigDto>(json, JsonOptions); }
        catch (JsonException) { return null; }
    }

    private static string? FirstSentence(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = System.Text.RegularExpressions.Regex.Split(text.Trim(), @"(?<=[.!?])\s+");
        return parts.Length == 0 ? null : parts[0].Trim();
    }

    private async Task<IReadOnlyList<ServiceImageVariantResponse>> GetReadyCoverVariantsAsync(Guid businessId, Guid? mediaId)
    {
        if (mediaId == null) return [];
        var variantsJson = await _context.Media.AsNoTracking()
            .Where(m => m.Id == mediaId.Value && m.BusinessId == businessId &&
                        m.Purpose == MediaPurposes.BusinessCover && m.Status == MediaStatus.Ready &&
                        m.Visibility == MediaVisibility.Public)
            .Select(m => m.VariantsJson)
            .FirstOrDefaultAsync();
        if (string.IsNullOrWhiteSpace(variantsJson)) return [];
        try
        {
            return (JsonSerializer.Deserialize<List<MediaVariantResponse>>(variantsJson, JsonOptions) ?? [])
                .Where(v => v.Format is "webp" or "jpeg")
                .Select(v => new ServiceImageVariantResponse { Url = v.Url, Width = v.Width, Height = v.Height, Format = v.Format })
                .ToList();
        }
        catch (JsonException) { return []; }
    }

    private static bool CapabilityAllows(string sectionKey, IReadOnlyDictionary<string, bool> capabilities)
    {
        return sectionKey.ToLowerInvariant() switch
        {
            "services" => capabilities.TryGetValue(CustomerCapabilityCatalog.Services, out var s) && s,
            "loyalty" => capabilities.TryGetValue(CustomerCapabilityCatalog.Loyalty, out var l) && l,
            "booking" => capabilities.TryGetValue(CustomerCapabilityCatalog.Appointments, out var a) && a,
            "reviews" => capabilities.TryGetValue(CustomerCapabilityCatalog.Reviews, out var r) && r,
            "gallery" => true,
            "about" => true,
            "contact" => true,
            "businessinfo" => true,
            _ => false,
        };
    }

    private static Dictionary<string, bool> FilterNavigation(
        LandingPageNavigationDto nav, IReadOnlyDictionary<string, bool> capabilities)
    {
        var raw = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            ["services"] = nav.Services,
            ["loyalty"] = nav.Loyalty,
            ["about"] = nav.About,
            ["reviews"] = nav.Reviews,
            ["contact"] = nav.Contact,
            ["gallery"] = nav.Gallery,
            ["booking"] = nav.Booking,
        };
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, enabled) in raw)
            result[key] = enabled && CapabilityAllows(key, capabilities);
        return result;
    }

    private static List<PublicLandingPageSection> FilterSections(
        Dictionary<string, LandingPageSectionDto> sections, IReadOnlyDictionary<string, bool> capabilities)
    {
        return sections
            .Where(kv => kv.Value != null && kv.Value.Enabled && CapabilityAllows(kv.Key, capabilities))
            .Select(kv => new PublicLandingPageSection
            {
                Key = kv.Key,
                Order = kv.Value.Order,
                Title = kv.Value.Title,
                Description = kv.Value.Description,
            })
            .OrderBy(s => s.Order)
            .ThenBy(s => s.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
