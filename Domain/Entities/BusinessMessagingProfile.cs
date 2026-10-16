using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PunchedApi.Domain.Entities;

/// <summary>
/// Per-business messaging profile (Phase 4). Holds branding, sender identity,
/// and reminder-policy defaults for a business. One row per business.
/// </summary>
[Table("business_messaging_profiles")]
public class BusinessMessagingProfile : BaseEntity
{
    /// <summary>Tenant scope — PK + FK to businesses.</summary>
    [Required]
    public Guid BusinessId { get; set; }

    /// <summary>Business display name used in email From header.</summary>
    [Required]
    [MaxLength(200)]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Reference to a media row in R2 for the logo.</summary>
    public Guid? LogoMediaId { get; set; }

    /// <summary>Primary brand color (hex, e.g. #FF5733).</summary>
    [MaxLength(20)]
    public string? PrimaryColor { get; set; }

    /// <summary>Reply-to email address. Must be a verified sender identity.</summary>
    [MaxLength(200)]
    public string? ReplyTo { get; set; }

    /// <summary>Footer note / sign-off text appended to emails.</summary>
    [MaxLength(1000)]
    public string? FooterNote { get; set; }

    /// <summary>Sender domain id for SPF/DKIM alignment (tenant subdomain).</summary>
    [MaxLength(200)]
    public string? SenderDomainId { get; set; }

    /// <summary>Business timezone (IANA id, e.g. Africa/Nairobi).</summary>
    [MaxLength(64)]
    public string? Timezone { get; set; }

    /// <summary>Reminder policy offsets in minutes (JSON array).</summary>
    [Required]
    public string ReminderPolicyJson { get; set; } = "[1440,60]";

    /// <summary>Foreign key to businesses.</summary>
    [ForeignKey(nameof(BusinessId))]
    public virtual Business? Business { get; set; }
}