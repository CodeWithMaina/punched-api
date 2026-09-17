using Microsoft.EntityFrameworkCore;
using PunchedApi.Application.DTOs;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Application.Services;

/// <summary>
/// Entitlement gating and enum/DTO mapping helpers for earning rules.
/// </summary>
public partial class LoyaltyEarningRuleService
{
    /// <summary>
    /// Whether the business holds the Referrals module. Cached for 60 s by
    /// <see cref="IModuleEntitlementService"/>, so this is cheap per request.
    /// </summary>
    private Task<bool> IsReferralAvailableAsync(Guid businessId) =>
        _entitlementService.IsModuleEnabledAsync(businessId, ReferralModuleKey);

    /// <summary>
    /// Gate for a rule's source module. Only Referral currently depends on a
    /// separate module; appointment and service earning are owned in-house.
    /// </summary>
    private async Task<(bool Available, string? Reason)> CheckSourceAvailabilityAsync(
        Guid businessId, EarningSource source)
    {
        if (source != EarningSource.Referral) return (true, null);

        if (await IsReferralAvailableAsync(businessId)) return (true, null);

        return (false,
            "Referral earning requires the Referrals module, which is not included in your current plan.");
    }

    /// <summary>Tenant-scoped program lookup. Never trusts a client-supplied business id.</summary>
    private Task<LoyaltyProgram?> ResolveProgramAsync(Guid businessId, Guid programId) =>
        _context.LoyaltyPrograms
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == programId && p.BusinessId == businessId);

    // ── Enum parsing (API uses readable lowercase strings) ──

    internal static bool TryParseSource(string? value, out EarningSource source)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "appointment": source = EarningSource.Appointment; return true;
            case "service": source = EarningSource.Service; return true;
            case "referral": source = EarningSource.Referral; return true;
            default: source = EarningSource.Appointment; return false;
        }
    }

    /// <summary>Null/absent falls back to Manual — the product default.</summary>
    internal static bool TryParseStampingMode(string? value, out StampingMode mode)
    {
        if (string.IsNullOrWhiteSpace(value)) { mode = StampingMode.Manual; return true; }

        switch (value.Trim().ToLowerInvariant())
        {
            case "manual": mode = StampingMode.Manual; return true;
            case "automatic": mode = StampingMode.Automatic; return true;
            default: mode = StampingMode.Manual; return false;
        }
    }

    /// <summary>Null/absent falls back to Draft — a rule is never live implicitly.</summary>
    internal static bool TryParseRuleStatus(string? value, out EarningRuleStatus status)
    {
        if (string.IsNullOrWhiteSpace(value)) { status = EarningRuleStatus.Draft; return true; }

        switch (value.Trim().ToLowerInvariant())
        {
            case "draft": status = EarningRuleStatus.Draft; return true;
            case "active": status = EarningRuleStatus.Active; return true;
            case "inactive": status = EarningRuleStatus.Inactive; return true;
            case "archived": status = EarningRuleStatus.Archived; return true;
            default: status = EarningRuleStatus.Draft; return false;
        }
    }

    // ── Mapping ──

    internal static string MapSource(EarningSource source) => source switch
    {
        EarningSource.Service => "service",
        EarningSource.Referral => "referral",
        _ => "appointment"
    };

    internal static string MapStampingMode(StampingMode mode) =>
        mode == StampingMode.Automatic ? "automatic" : "manual";

    internal static string MapRuleStatus(EarningRuleStatus status) => status switch
    {
        EarningRuleStatus.Active => "active",
        EarningRuleStatus.Inactive => "inactive",
        EarningRuleStatus.Archived => "archived",
        _ => "draft"
    };

    /// <summary>
    /// Projects a rule, annotating whether its source module is entitled so the
    /// UI can show a genuine subscription-gated state rather than guessing.
    /// </summary>
    internal static EarningRuleResponse MapRule(LoyaltyEarningRule rule, bool referralAvailable)
    {
        var available = rule.Source != EarningSource.Referral || referralAvailable;

        return new EarningRuleResponse
        {
            Id = rule.Id,
            ProgramId = rule.ProgramId,
            BusinessId = rule.BusinessId,
            Source = MapSource(rule.Source),
            StampAmount = rule.StampAmount,
            StampingMode = MapStampingMode(rule.StampingMode),
            Status = MapRuleStatus(rule.Status),
            Description = rule.Description,
            QualifyingServiceId = rule.QualifyingServiceId,
            ActivatedAt = rule.ActivatedAt,
            CreatedAt = rule.CreatedAt,
            IsAvailable = available,
            UnavailableReason = available
                ? null
                : "Referral earning requires the Referrals module, which is not included in your current plan."
        };
    }
}