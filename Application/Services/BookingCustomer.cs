namespace PunchedApi.Application.Services;

/// <summary>
/// The party data a booking write needs, resolved once by the caller.
///
/// <para>Passing it into the write is what lets the storefront fast path avoid re-reading a
/// customer, an enrollment and a business it has already resolved. It is display data only:
/// nothing here is trusted for authorization.</para>
/// </summary>
public sealed record BookingCustomer(
    Guid Id,
    string? FullName,
    string? Email,
    string? PhoneNumber,
    string? AvatarUrl,
    string CustomerType);
