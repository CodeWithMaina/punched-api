using System.Text.Json.Serialization;

namespace PunchedApi.Application.DTOs;

// ═══════════════════════════════════════════════════════════════
//  PLATFORM ADMIN — BUSINESS MANAGEMENT DTOs
//  The DB entity stays Business; these are the API/UI boundary.
// ═══════════════════════════════════════════════════════════════

/// <summary>Roll-up counters for the business-management dashboard header.</summary>
public class AdminBusinessStats
{
    [JsonPropertyName("total")] public int Total { get; set; }
    [JsonPropertyName("active")] public int Active { get; set; }
    [JsonPropertyName("suspended")] public int Suspended { get; set; }
    [JsonPropertyName("deactivated")] public int Deactivated { get; set; }
    [JsonPropertyName("trial")] public int Trial { get; set; }
    [JsonPropertyName("activeSubscriptions")] public int ActiveSubscriptions { get; set; }
    [JsonPropertyName("expiredSubscriptions")] public int ExpiredSubscriptions { get; set; }
    /// <summary>Active subscriptions whose period ends within the next 7 days.</summary>
    [JsonPropertyName("expiringSoon")] public int ExpiringSoon { get; set; }
    [JsonPropertyName("noSubscription")] public int NoSubscription { get; set; }
}

/// <summary>One row in the admin business list.</summary>
public class AdminBusinessListItem
{
    [JsonPropertyName("id")] public Guid Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("category")] public string Category { get; set; } = string.Empty;
    [JsonPropertyName("location")] public string Location { get; set; } = string.Empty;
    [JsonPropertyName("logoUrl")] public string? LogoUrl { get; set; }
    [JsonPropertyName("slug")] public string? Slug { get; set; }
    [JsonPropertyName("tenantUrl")] public string? TenantUrl { get; set; }
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonPropertyName("phoneNumber")] public string? PhoneNumber { get; set; }

    /// <summary>Platform-administration status: active | suspended | deactivated.</summary>
    [JsonPropertyName("status")] public string Status { get; set; } = "active";

    [JsonPropertyName("ownerId")] public Guid? OwnerId { get; set; }
    [JsonPropertyName("ownerName")] public string OwnerName { get; set; } = string.Empty;
    [JsonPropertyName("ownerEmail")] public string OwnerEmail { get; set; } = string.Empty;

    [JsonPropertyName("planKey")] public string? PlanKey { get; set; }
    [JsonPropertyName("planName")] public string? PlanName { get; set; }
    [JsonPropertyName("subscriptionStatus")] public string? SubscriptionStatus { get; set; }
    [JsonPropertyName("subscriptionEndsAt")] public DateTime? SubscriptionEndsAt { get; set; }

    /// <summary>Module keys the business currently has effective access to.</summary>
    [JsonPropertyName("enabledModules")] public List<string> EnabledModules { get; set; } = new();

    [JsonPropertyName("totalStaff")] public int TotalStaff { get; set; }
    [JsonPropertyName("totalCustomers")] public int TotalCustomers { get; set; }
    [JsonPropertyName("totalStamps")] public int TotalStamps { get; set; }

    /// <summary>UTC timestamp of the business's most recent recorded activity.</summary>
    [JsonPropertyName("lastActivityAt")] public DateTime? LastActivityAt { get; set; }
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; }
}

/// <summary>Owner block inside a business detail response.</summary>
public class AdminBusinessOwnerInfo
{
    [JsonPropertyName("id")] public Guid? Id { get; set; }
    [JsonPropertyName("fullName")] public string FullName { get; set; } = string.Empty;
    [JsonPropertyName("email")] public string Email { get; set; } = string.Empty;
    [JsonPropertyName("phoneNumber")] public string? PhoneNumber { get; set; }
    [JsonPropertyName("avatarUrl")] public string? AvatarUrl { get; set; }
}

/// <summary>Subscription block inside a business detail response.</summary>
public class AdminBusinessSubscriptionInfo
{
    [JsonPropertyName("subscriptionId")] public Guid? SubscriptionId { get; set; }
    [JsonPropertyName("planId")] public Guid? PlanId { get; set; }
    [JsonPropertyName("planKey")] public string? PlanKey { get; set; }
    [JsonPropertyName("planName")] public string? PlanName { get; set; }
    [JsonPropertyName("billingInterval")] public string? BillingInterval { get; set; }
    [JsonPropertyName("price")] public decimal? Price { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("startsAt")] public DateTime? StartsAt { get; set; }
    [JsonPropertyName("currentPeriodStartsAt")] public DateTime? CurrentPeriodStartsAt { get; set; }
    [JsonPropertyName("endsAt")] public DateTime? EndsAt { get; set; }
    [JsonPropertyName("canceledAt")] public DateTime? CanceledAt { get; set; }
    [JsonPropertyName("pausedAt")] public DateTime? PausedAt { get; set; }
    [JsonPropertyName("pauseReason")] public string? PauseReason { get; set; }
    [JsonPropertyName("isTrial")] public bool IsTrial { get; set; }
    [JsonPropertyName("daysUntilExpiry")] public int? DaysUntilExpiry { get; set; }
}

/// <summary>Full business detail (business detail page).</summary>
public class AdminBusinessDetailResponse
{
    [JsonPropertyName("id")] public Guid Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("category")] public string Category { get; set; } = string.Empty;
    [JsonPropertyName("location")] public string Location { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("logoUrl")] public string? LogoUrl { get; set; }
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonPropertyName("phoneNumber")] public string? PhoneNumber { get; set; }
    [JsonPropertyName("mpesaNumber")] public string? MpesaNumber { get; set; }
    [JsonPropertyName("timeZoneId")] public string? TimeZoneId { get; set; }
    [JsonPropertyName("slug")] public string? Slug { get; set; }
    [JsonPropertyName("tenantUrl")] public string? TenantUrl { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = "active";
    [JsonPropertyName("suspendedAt")] public DateTime? SuspendedAt { get; set; }
    [JsonPropertyName("suspendedReason")] public string? SuspendedReason { get; set; }
    [JsonPropertyName("deactivatedAt")] public DateTime? DeactivatedAt { get; set; }
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; }
    [JsonPropertyName("lastActivityAt")] public DateTime? LastActivityAt { get; set; }

    [JsonPropertyName("owner")] public AdminBusinessOwnerInfo Owner { get; set; } = new();
    [JsonPropertyName("subscription")] public AdminBusinessSubscriptionInfo? Subscription { get; set; }

    [JsonPropertyName("totalStaff")] public int TotalStaff { get; set; }
    [JsonPropertyName("totalCustomers")] public int TotalCustomers { get; set; }
    [JsonPropertyName("totalStamps")] public int TotalStamps { get; set; }
    [JsonPropertyName("totalRedemptions")] public int TotalRedemptions { get; set; }
    [JsonPropertyName("programCount")] public int ProgramCount { get; set; }
}

/// <summary>Body of PUT /v1/admin/businesses/{id} — admin profile edit.</summary>
public class AdminBusinessUpdateRequest
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("category")] public string? Category { get; set; }
    [JsonPropertyName("location")] public string? Location { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonPropertyName("phoneNumber")] public string? PhoneNumber { get; set; }
    [JsonPropertyName("mpesaNumber")] public string? MpesaNumber { get; set; }
    [JsonPropertyName("logoUrl")] public string? LogoUrl { get; set; }
    [JsonPropertyName("timeZoneId")] public string? TimeZoneId { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
}

/// <summary>Body of the business suspend/deactivate endpoints.</summary>
public class AdminBusinessStatusRequest
{
    /// <summary>Administrative reason (recorded in the audit log).</summary>
    [JsonPropertyName("reason")] public string? Reason { get; set; }
}

/// <summary>One member of a business (owner/staff/customer).</summary>
public class AdminBusinessUserItem
{
    [JsonPropertyName("userId")] public Guid UserId { get; set; }
    [JsonPropertyName("fullName")] public string FullName { get; set; } = string.Empty;
    [JsonPropertyName("email")] public string Email { get; set; } = string.Empty;
    [JsonPropertyName("phoneNumber")] public string? PhoneNumber { get; set; }
    /// <summary>Owner | Staff | Customer.</summary>
    [JsonPropertyName("role")] public string Role { get; set; } = string.Empty;
    /// <summary>Active | Invited | Suspended | Left.</summary>
    [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
    [JsonPropertyName("joinedAt")] public DateTime JoinedAt { get; set; }
    [JsonPropertyName("endedAt")] public DateTime? EndedAt { get; set; }
    /// <summary>Global platform role of the identity (Customer/Business/Staff/Admin).</summary>
    [JsonPropertyName("globalRole")] public string GlobalRole { get; set; } = string.Empty;
    [JsonPropertyName("isVerified")] public bool IsVerified { get; set; }
}

// ═══════════════════════════════════════════════════════════════
//  PLATFORM ADMIN — SUBSCRIPTION MANAGEMENT DTOs
// ═══════════════════════════════════════════════════════════════

/// <summary>Roll-up counters for the subscription-management dashboard header.</summary>
public class AdminSubscriptionStats
{
    [JsonPropertyName("total")] public int Total { get; set; }
    [JsonPropertyName("active")] public int Active { get; set; }
    [JsonPropertyName("trial")] public int Trial { get; set; }
    [JsonPropertyName("paused")] public int Paused { get; set; }
    [JsonPropertyName("canceled")] public int Canceled { get; set; }
    [JsonPropertyName("expired")] public int Expired { get; set; }
    [JsonPropertyName("pastDue")] public int PastDue { get; set; }
    [JsonPropertyName("deactivated")] public int Deactivated { get; set; }
    [JsonPropertyName("renewingIn7Days")] public int RenewingIn7Days { get; set; }
    /// <summary>Monthly recurring revenue from active monthly plans (KES).</summary>
    [JsonPropertyName("monthlyRecurringRevenue")] public decimal MonthlyRecurringRevenue { get; set; }
    /// <summary>Annual recurring revenue: MRR×12 plus active yearly plan value (KES).</summary>
    [JsonPropertyName("annualRecurringRevenue")] public decimal AnnualRecurringRevenue { get; set; }
}

/// <summary>One row in the admin subscription list.</summary>
public class AdminSubscriptionListItem
{
    [JsonPropertyName("subscriptionId")] public Guid SubscriptionId { get; set; }
    [JsonPropertyName("businessId")] public Guid BusinessId { get; set; }
    [JsonPropertyName("businessName")] public string BusinessName { get; set; } = string.Empty;
    [JsonPropertyName("businessStatus")] public string BusinessStatus { get; set; } = "active";
    [JsonPropertyName("planId")] public Guid PlanId { get; set; }
    [JsonPropertyName("planKey")] public string PlanKey { get; set; } = string.Empty;
    [JsonPropertyName("planName")] public string PlanName { get; set; } = string.Empty;
    [JsonPropertyName("billingInterval")] public string BillingInterval { get; set; } = "monthly";
    [JsonPropertyName("price")] public decimal Price { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = "active";
    [JsonPropertyName("startsAt")] public DateTime? StartsAt { get; set; }
    [JsonPropertyName("endsAt")] public DateTime? EndsAt { get; set; }
    [JsonPropertyName("pausedAt")] public DateTime? PausedAt { get; set; }
    [JsonPropertyName("canceledAt")] public DateTime? CanceledAt { get; set; }
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; }
    /// <summary>Modules included by the subscription's plan (plan module count).</summary>
    [JsonPropertyName("enabledModuleCount")] public int EnabledModuleCount { get; set; }
}

/// <summary>Body of the subscription pause/cancel/deactivate endpoints.</summary>
public class AdminSubscriptionActionRequest
{
    [JsonPropertyName("reason")] public string? Reason { get; set; }
}

/// <summary>Body of POST /v1/admin/subscriptions/{businessId}/extend.</summary>
public class AdminExtendSubscriptionRequest
{
    /// <summary>New period end (UTC). Must be in the future.</summary>
    [JsonPropertyName("endsAt")] public DateTime EndsAt { get; set; }

    [JsonPropertyName("reason")] public string? Reason { get; set; }
}

/// <summary>Result of a subscription lifecycle action.</summary>
public class AdminSubscriptionActionResult
{
    [JsonPropertyName("businessId")] public Guid BusinessId { get; set; }
    [JsonPropertyName("affectedRows")] public int AffectedRows { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("endsAt")] public DateTime? EndsAt { get; set; }
    [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;
}

/// <summary>Body of POST /v1/admin/subscription-tiers/{id}/duplicate.</summary>
public class AdminDuplicateTierRequest
{
    /// <summary>New unique tier key (lowercase, e.g. "growth-copy").</summary>
    [JsonPropertyName("key")] public string? Key { get; set; }

    /// <summary>New display name; defaults to "&lt;source&gt; (copy)".</summary>
    [JsonPropertyName("name")] public string? Name { get; set; }
}

/// <summary>Body of PUT /v1/admin/modules/{key} — module catalog metadata edit.</summary>
public class AdminModuleUpdateRequest
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("isActive")] public bool? IsActive { get; set; }
    [JsonPropertyName("reason")] public string? Reason { get; set; }
}

/// <summary>One row of the admin module catalogue.</summary>
public class AdminModuleCatalogItem
{
    [JsonPropertyName("key")] public string Key { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("isCore")] public bool IsCore { get; set; }
    [JsonPropertyName("visibility")] public string Visibility { get; set; } = "Standard";
    [JsonPropertyName("version")] public string Version { get; set; } = "1.0.0";
    [JsonPropertyName("isActive")] public bool IsActive { get; set; } = true;
    [JsonPropertyName("dependencies")] public List<string> Dependencies { get; set; } = new();
    /// <summary>Number of plans that include this module.</summary>
    [JsonPropertyName("planCount")] public int PlanCount { get; set; }
    /// <summary>Number of businesses with an effective entitlement to this module.</summary>
    [JsonPropertyName("businessCount")] public int BusinessCount { get; set; }
}

/// <summary>Response of GET /v1/admin/modules/catalog (module catalogue management).</summary>
public class AdminModuleCatalogResponse
{
    [JsonPropertyName("modules")] public List<AdminModuleCatalogItem> Modules { get; set; } = new();
}
