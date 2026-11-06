using System.ComponentModel.DataAnnotations;

namespace PunchedApi.Domain.Entities;

/// <summary>
/// A business-scoped, many-to-many link assigning a provider (staff member or
/// the business owner) to a <see cref="ServiceCategory"/> they specialise in.
///
/// <para><b>This row is the primary source of provider eligibility.</b> A
/// provider assigned to a category is automatically eligible to perform
/// <b>every active service</b> in that category — including services created
/// later — so owners never hand-assign individual services. Category
/// relationships resolve eligibility directly, which keeps the model cheap
/// (one row per provider/category) instead of materialising one row per
/// provider/service.</para>
///
/// <para><see cref="StaffServiceAssignment"/> is retained for two reasons:
/// (1) it is the safe fallback for grandfathered services that have no category
/// yet, and (2) it preserves historical assignments created before categories
/// existed. Eliminating an individual service from a provider is done by
/// removing the CATEGORY assignment, not by editing service rows.</para>
/// </summary>
public class StaffServiceCategory : BaseEntity
{
    /// <summary>The provider identity (an active staff member OR the business owner).</summary>
    [Required]
    public Guid StaffUserId { get; set; }

    /// <summary>The service category the provider specialises in.</summary>
    [Required]
    public Guid ServiceCategoryId { get; set; }

    /// <summary>Tenant scope. Every query filters on this, so isolation is never implicit.</summary>
    [Required]
    public Guid BusinessId { get; set; }
}
