using System.Text.Json;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PunchedApi.Application.Authorization;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Modules;
using PunchedApi.Domain.Entities;
using PunchedApi.Domain.Interfaces;

namespace PunchedApi.Application.Services;

public partial class BusinessService
{
    /// <summary>
    /// Customer-dashboard bootstrap: ONE call returning the minimum data every
    /// customer dashboard shell render needs — resolved business context, the
    /// customer's relationship to that business, effective customer
    /// capabilities (derived from module entitlements via
    /// <see cref="CustomerCapabilityCatalog"/>), module-driven navigation and
    /// a lightweight homepage summary.
    ///
    /// Resolved server-side from the authenticated customer (the client never
    /// supplies a business id). On a tenant host the business is resolved from
    /// the host; on the platform root it is the customer's most recent active
    /// association. Disabled modules are an expected state — they simply do not
    /// appear in capabilities or navigation; this is never an error.
    /// </summary>
    public async Task<ApiResponse<CustomerDashboardBootstrapResponse>> GetCustomerDashboardBootstrapAsync(Guid customerId)
    {
        // ── 1. Resolve business context ────────────────────────────────────
        // Tenant host wins (the address IS the business); otherwise fall back
        // to the customer's most recent active association.
        Guid? businessId = null;
        bool resolvedFromTenant = false;

        if (_tenant?.IsActive == true)
        {
            businessId = _tenant.BusinessId;
            resolvedFromTenant = true;
        }
        else
        {
            var association = await _context.CustomerBusinessEnrollments
                .AsNoTracking()
                .Where(e => e.CustomerId == customerId && e.Status == CustomerBusinessEnrollmentStatus.Active)
                .OrderByDescending(e => e.EnrolledAt)
                .Select(e => new { e.BusinessId })
                .FirstOrDefaultAsync();

            if (association == null)
                return ApiResponse<CustomerDashboardBootstrapResponse>.Fail(
                    "NO_BUSINESS", "Customer is not associated with any business.");

            businessId = association.BusinessId;
        }

        // Both resolution branches above assign an id; asserting it here keeps
        // the rest of the method on a non-nullable value (and fails fast if a
        // future branch forgets to resolve one).
        if (businessId is not { } resolvedBusinessId)
            return ApiResponse<CustomerDashboardBootstrapResponse>.Fail(
                "NO_BUSINESS", "Customer is not associated with any business.");

        // ── 2. Verify customer membership (authorization boundary) ─────────
        var membership = await _businessMembershipResolver.ResolveAsync(customerId, resolvedBusinessId);
        if (!membership.IsActive)
            return ApiResponse<CustomerDashboardBootstrapResponse>.Fail(
                "FORBIDDEN", "Customer is not a member of this business.");

        // ── 3. Load business identity (projection only, no entity graph) ───
        var business = await _context.Businesses
            .AsNoTracking()
            .Where(b => b.Id == resolvedBusinessId && !b.IsDeleted)
            .Select(b => new
            {
                b.Id,
                b.Name,
                b.Slug,
                b.Category,
                b.Location,
                b.Description,
                b.LogoUrl,
                b.PhoneNumber,
                b.Email,
                HasLoyaltyProgram = b.LoyaltyPrograms.Any(p => p.IsActive && p.Status == ProgramStatus.Active),
                HasReferralProgram = b.ReferralProgram != null && b.ReferralProgram.IsActive,
            })
            .FirstOrDefaultAsync();

        if (business == null)
            return ApiResponse<CustomerDashboardBootstrapResponse>.Fail("NOT_FOUND", "Business not found.");

        // ── 4. Effective module keys → customer capabilities ───────────────
        // Reuse the same entitlement resolution [RequireModule] uses, then map
        // modules → customer capabilities via CustomerCapabilityCatalog. One
        // cached call (60 s TTL inside ModuleEntitlementService).
        var effectiveModuleKeys = await _moduleEntitlementService.GetEffectiveModuleKeysAsync(resolvedBusinessId);
        var capabilities = CustomerCapabilityCatalog.Resolve(
            effectiveModuleKeys,
            hasActiveLoyaltyProgram: business.HasLoyaltyProgram,
            hasActiveReferralProgram: business.HasReferralProgram)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);

        var enabledModules = capabilities
            .Where(kvp => kvp.Value)
            .Select(kvp => kvp.Key)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        // ── 5. Module-driven navigation (customer shell scope) ─────────────
        var tabs = BuildCustomerNavigationTabs(enabledModules);

        // ── 6. Lightweight homepage summary (counts only, bounded payload) ─
        var upcomingCount = await _context.Appointments
            .AsNoTracking()
            .Where(a => a.BusinessId == resolvedBusinessId && a.CustomerId == customerId
                && (a.Status == "booked" || a.Status == "confirmed"))
            .CountAsync();

        var loyaltyCardCount = await _context.LoyaltyCards
            .AsNoTracking()
            .Where(l => l.CustomerId == customerId && l.BusinessId == resolvedBusinessId)
            .CountAsync();

        var serviceCount = await _context.ServiceCatalogItems
            .AsNoTracking()
            .Where(s => s.BusinessId == resolvedBusinessId && s.IsActive)
            .CountAsync();

        // ── 7. Enrich customer relationship (enrollment source/date) ───────
        var relationship = new CustomerCustomerRelationship
        {
            IsMember = true,
            Status = membership.Status,
            Source = null,
            EnrolledAt = null,
            ResolvedFromTenant = resolvedFromTenant,
        };

        if (!resolvedFromTenant)
        {
            var enrollment = await _context.CustomerBusinessEnrollments
                .AsNoTracking()
                .Where(e => e.CustomerId == customerId && e.BusinessId == resolvedBusinessId)
                .Select(e => new { e.Source, e.EnrolledAt })
                .FirstOrDefaultAsync();
            if (enrollment != null)
            {
                relationship.Source = enrollment.Source;
                relationship.EnrolledAt = enrollment.EnrolledAt;
            }
        }

        // ── 8. Assemble response ───────────────────────────────────────────
        return ApiResponse<CustomerDashboardBootstrapResponse>.Ok(new CustomerDashboardBootstrapResponse
        {
            Business = new CustomerDashboardBusiness
            {
                Id = business.Id,
                Name = business.Name,
                Slug = business.Slug ?? string.Empty,
                Category = business.Category,
                Location = business.Location,
                Description = business.Description,
                LogoUrl = business.LogoUrl,
                PhoneNumber = business.PhoneNumber,
                Email = business.Email,
                HasLoyaltyProgram = business.HasLoyaltyProgram,
                HasReferralProgram = business.HasReferralProgram,
            },
            Relationship = relationship,
            Capabilities = capabilities,
            EnabledModules = enabledModules,
            Navigation = new CustomerDashboardNavigation
            {
                Tabs = tabs,
                EnabledModules = enabledModules,
            },
            Summary = new CustomerDashboardSummary
            {
                UpcomingAppointmentCount = upcomingCount,
                LoyaltyCardCount = loyaltyCardCount,
                ServiceCount = serviceCount,
            },
        });
    }

    /// <summary>
    /// Customer shell tab order — pruned to the capabilities this business
    /// actually exposes. Disabled capabilities simply do not appear.
    /// </summary>
    private static List<string> BuildCustomerNavigationTabs(List<string> enabledModules)
    {
        // Customer-facing navigation order, mirroring
        // registry/customerCapabilities.ts CUSTOMER_CAPABILITY_ORDER.
        var customerShellOrder = new[]
        {
            "services",
            "appointments",
            "loyalty",
            "rewards",
            "reviews",
            "gallery",
            "referrals",
            "payments",
            "notifications",
        };

        var tabs = new List<string>();
        foreach (var key in customerShellOrder)
        {
            if (enabledModules.Contains(key, StringComparer.OrdinalIgnoreCase))
                tabs.Add(key);
        }
        return tabs;
    }
}