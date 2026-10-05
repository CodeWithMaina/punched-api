namespace PunchedApi.Application.Services;

/// <summary>
/// Names of the optional tenant claims carried by an access token.
///
/// These are the ONLY place the claim names are defined on the backend; the
/// frontend mirror lives in <c>punched-pwd/lib/tenant.ts</c>. Both are optional
/// and additive: a token without them behaves exactly as tokens always did.
/// </summary>
public static class TenantClaimNames
{
    /// <summary>Business id the caller was authenticated in ("biz").</summary>
    public const string BusinessId = "biz";

    /// <summary>Role within that business ("bizRole"): Owner | Staff | Customer.</summary>
    public const string BusinessRole = "bizRole";

    /// <summary>Owner of a business.</summary>
    public const string RoleOwner = "Owner";

    /// <summary>Staff member of a business.</summary>
    public const string RoleStaff = "Staff";

    /// <summary>Enrolled customer of a business.</summary>
    public const string RoleCustomer = "Customer";
}
