namespace PunchedApi.Application.DTOs;

// ════════════════════════════════════════════════════════════════
//  CUSTOMER DASHBOARD BOOTSTRAP DTOs
//  GET /v1/customers/me/dashboard/bootstrap — single cold-load call returning
//  business context, customer relationship, capabilities, navigation and a
//  lightweight homepage summary for the customer dashboard shell.
// ════════════════════════════════════════════════════════════════

using System.Text.Json.Serialization;

/// <summary>Response of GET /v1/customers/me/dashboard/bootstrap.</summary>
public class CustomerDashboardBootstrapResponse
{
    [JsonPropertyName("business")]
    public CustomerDashboardBusiness Business { get; set; } = new();

    [JsonPropertyName("relationship")]
    public CustomerCustomerRelationship Relationship { get; set; } = new();

    [JsonPropertyName("capabilities")]
    public Dictionary<string, bool> Capabilities { get; set; } = new();

    [JsonPropertyName("enabledModules")]
    public List<string> EnabledModules { get; set; } = new();

    [JsonPropertyName("navigation")]
    public CustomerDashboardNavigation Navigation { get; set; } = new();

    [JsonPropertyName("summary")]
    public CustomerDashboardSummary Summary { get; set; } = new();
}

/// <summary>Resolved business context for the customer dashboard.</summary>
public class CustomerDashboardBusiness
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("slug")]
    public string Slug { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("location")]
    public string? Location { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("logoUrl")]
    public string? LogoUrl { get; set; }

    [JsonPropertyName("phoneNumber")]
    public string? PhoneNumber { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("hasLoyaltyProgram")]
    public bool HasLoyaltyProgram { get; set; }

    [JsonPropertyName("hasReferralProgram")]
    public bool HasReferralProgram { get; set; }
}

/// <summary>The authenticated customer's relationship to the resolved business.</summary>
public class CustomerCustomerRelationship
{
    [JsonPropertyName("isMember")]
    public bool IsMember { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("enrolledAt")]
    public DateTime? EnrolledAt { get; set; }

    [JsonPropertyName("resolvedFromTenant")]
    public bool ResolvedFromTenant { get; set; }
}

/// <summary>Module-driven navigation for the customer dashboard shell.</summary>
public class CustomerDashboardNavigation
{
    [JsonPropertyName("tabs")]
    public List<string> Tabs { get; set; } = new();

    [JsonPropertyName("enabledModules")]
    public List<string> EnabledModules { get; set; } = new();
}

/// <summary>Lightweight homepage summary data required for the initial render.</summary>
public class CustomerDashboardSummary
{
    [JsonPropertyName("upcomingAppointmentCount")]
    public int UpcomingAppointmentCount { get; set; }

    [JsonPropertyName("loyaltyCardCount")]
    public int LoyaltyCardCount { get; set; }

    [JsonPropertyName("serviceCount")]
    public int ServiceCount { get; set; }
}