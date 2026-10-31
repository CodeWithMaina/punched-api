using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PunchedApi.Domain.Entities;
using PunchedApi.Infrastructure.Data;

namespace PunchedApi.Application.Services;

/// <summary>
/// The commission arrangement that applies to one (staff, service) pair, with
/// the calculation context that must be frozen onto the ledger entry.
/// </summary>
public sealed record ResolvedCommissionArrangement(
    CommissionScope Scope,
    Guid? ConfigId,
    CommissionCalculationType CalculationType,
    decimal? Percentage,
    decimal? FixedAmount,
    string Currency,
    int RoundingDecimals,
    bool IsEnabled,
    bool RequireSettlement,
    bool RequireApproval,
    bool EarnOnNoShow,
    bool EarnOnCancelled,
    bool AllowPartialSettlement,
    decimal MinimumSettlementRatio,
    bool RequireStaffServiceEligibility,
    string SnapshotJson);

/// <summary>
/// <b>Commission precedence resolution.</b>
///
/// <para>Arrangements are ranked by <b>specificity</b>, most specific first:</para>
/// <code>
/// staff + service  (CommissionScope.StaffService)   — wins
/// service          (CommissionScope.Service)
/// staff            (CommissionScope.Staff)
/// business default (CommissionConfig)               — fallback
/// </code>
///
/// <para>Only the single most specific ACTIVE arrangement applies; there is no
/// blending, no stacking and no "first match wins" ambiguity. Conflicting
/// arrangements for the same scope cannot exist at all: the unique index on
/// <c>(business_id, scope_key)</c> rejects them, and the service surfaces that
/// as a clear validation error before the insert is attempted.</para>
///
/// <para>The resolution set is loaded once per business and reused across every
/// service line of an appointment, so accruing commission for a multi-service
/// appointment costs one query rather than one query per line.</para>
/// </summary>
public interface ICommissionResolver
{
    /// <summary>Loads the whole configuration for a business (one query each for config and rates).</summary>
    Task<CommissionResolutionSet> LoadAsync(Guid businessId, CancellationToken cancellationToken = default);
}

/// <summary>
/// <b>The commission amount calculation.</b> Pure, side-effect free and the only
/// place an amount is ever derived, so the ledger and the preview/report paths
/// can never disagree.
/// </summary>
public static class CommissionCalculator
{
    /// <summary>
    /// Applies the arrangement to a base amount. A percentage is applied to the
    /// base and rounded to the configured precision (half away from zero, the
    /// convention the rest of the money code uses); a fixed arrangement ignores
    /// the base entirely.
    /// </summary>
    public static decimal Calculate(
        CommissionCalculationType calculationType,
        decimal? percentage,
        decimal? fixedAmount,
        decimal baseAmount,
        int roundingDecimals)
    {
        var raw = calculationType switch
        {
            CommissionCalculationType.Fixed => fixedAmount ?? 0m,
            _ => baseAmount * (percentage ?? 0m) / 100m
        };

        return Round(raw, roundingDecimals);
    }

    /// <summary>Rounds a monetary value to the configured precision.</summary>
    public static decimal Round(decimal value, int roundingDecimals)
    {
        var decimals = Math.Clamp(roundingDecimals, 0, 4);
        return decimal.Round(value, decimals, MidpointRounding.AwayFromZero);
    }
}

/// <summary>A business's commission configuration, indexed for in-memory resolution.</summary>
public sealed class CommissionResolutionSet
{
    private readonly CommissionConfig? _config;
    private readonly Dictionary<(Guid? Staff, Guid? Service), CommissionRate> _rates;

    internal CommissionResolutionSet(CommissionConfig? config, IEnumerable<CommissionRate> rates)
    {
        _config = config;
        _rates = new Dictionary<(Guid?, Guid?), CommissionRate>();
        foreach (var rate in rates)
        {
            if (!rate.IsActive) continue;
            var key = rate.Scope switch
            {
                CommissionScope.StaffService => ((Guid?)rate.StaffUserId, (Guid?)rate.ServiceCatalogItemId),
                CommissionScope.Staff => ((Guid?)rate.StaffUserId, (Guid?)null),
                CommissionScope.Service => ((Guid?)null, (Guid?)rate.ServiceCatalogItemId),
                _ => ((Guid?)null, (Guid?)null)
            };
            _rates[key] = rate;
        }
    }

    /// <summary>True when the business has commissions switched on.</summary>
    public bool IsEnabled => _config?.IsEnabled == true;

    /// <summary>The raw configuration row (may be null when the business never configured commissions).</summary>
    public CommissionConfig? Config => _config;

    /// <summary>
    /// The arrangement that applies to the given staff member and service, or
    /// null when commissions are disabled for the business.
    /// </summary>
    public ResolvedCommissionArrangement? Resolve(Guid staffUserId, Guid? serviceId)
    {
        if (_config == null || !_config.IsEnabled) return null;

        var rate = FindRate(staffUserId, serviceId);
        var scope = rate?.Scope ?? CommissionScope.Business;

        return new ResolvedCommissionArrangement(
            Scope: scope,
            ConfigId: rate?.Id ?? _config.Id,
            CalculationType: rate?.CalculationType ?? _config.CalculationType,
            Percentage: rate?.Percentage ?? _config.DefaultPercentage,
            FixedAmount: rate?.FixedAmount ?? _config.DefaultFixedAmount,
            Currency: _config.Currency,
            RoundingDecimals: _config.RoundingDecimals,
            IsEnabled: true,
            RequireSettlement: _config.RequireSettlement,
            RequireApproval: _config.RequireApproval,
            EarnOnNoShow: _config.EarnOnNoShow,
            EarnOnCancelled: _config.EarnOnCancelled,
            AllowPartialSettlement: _config.AllowPartialSettlement,
            MinimumSettlementRatio: _config.MinimumSettlementRatio,
            RequireStaffServiceEligibility: _config.RequireStaffServiceEligibility,
            SnapshotJson: BuildSnapshot(rate, _config));
    }

    private CommissionRate? FindRate(Guid staffUserId, Guid? serviceId)
    {
        if (serviceId.HasValue && _rates.TryGetValue((staffUserId, serviceId.Value), out var both)) return both;
        if (serviceId.HasValue && _rates.TryGetValue((null, serviceId.Value), out var serviceOnly)) return serviceOnly;
        if (_rates.TryGetValue((staffUserId, null), out var staffOnly)) return staffOnly;
        return null;
    }

    /// <summary>
    /// The frozen calculation context stored on every entry. Records BOTH the
    /// business default and the override that actually applied, so a later
    /// configuration change is provably not what produced a historical amount.
    /// </summary>
    private static string BuildSnapshot(CommissionRate? rate, CommissionConfig config) =>
        JsonSerializer.Serialize(new
        {
            configId = config.Id,
            configCurrency = config.Currency,
            configCalculationType = config.CalculationType.ToString(),
            configPercentage = config.DefaultPercentage,
            configFixedAmount = config.DefaultFixedAmount,
            roundingDecimals = config.RoundingDecimals,
            requireSettlement = config.RequireSettlement,
            requireApproval = config.RequireApproval,
            requireStaffServiceEligibility = config.RequireStaffServiceEligibility,
            appliedScope = (rate?.Scope ?? CommissionScope.Business).ToString(),
            appliedRateId = rate?.Id,
            appliedCalculationType = (rate?.CalculationType ?? config.CalculationType).ToString(),
            appliedPercentage = rate?.Percentage ?? config.DefaultPercentage,
            appliedFixedAmount = rate?.FixedAmount ?? config.DefaultFixedAmount
        });
}

/// <inheritdoc />
public sealed class CommissionResolver : ICommissionResolver
{
    private readonly ApplicationDbContext _context;

    public CommissionResolver(ApplicationDbContext context)
    {
        _context = context;
    }

    /// <inheritdoc />
    public async Task<CommissionResolutionSet> LoadAsync(
        Guid businessId, CancellationToken cancellationToken = default)
    {
        var config = await _context.CommissionConfigs
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.BusinessId == businessId, cancellationToken);

        var rates = await _context.CommissionRates
            .AsNoTracking()
            .Where(r => r.BusinessId == businessId && r.IsActive)
            .ToListAsync(cancellationToken);

        return new CommissionResolutionSet(config, rates);
    }
}
