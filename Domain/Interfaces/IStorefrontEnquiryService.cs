using PunchedApi.Application.DTOs;

namespace PunchedApi.Domain.Interfaces;

/// <summary>
/// Storefront enquiries: an anonymous visitor's question, delivered to the
/// business that owns the page they were reading.
///
/// <para>Deliberately narrow. The platform records the enquiry and tells the
/// owner; it does not create a customer, does not open a conversation, and does
/// not promise the visitor a reply. Those would each be a feature with its own
/// consent and identity requirements, and faking any of them here would make the
/// storefront's confirmation untrue.</para>
/// </summary>
public interface IStorefrontEnquiryService
{
    /// <summary>
    /// Records one enquiry against a business and notifies its owner.
    ///
    /// Resolves the business by id, refuses an unknown or inactive one, and
    /// rejects a business with no owner (nothing could receive it). Never throws
    /// for a notification failure — the enquiry is still recorded, and the
    /// visitor is told it was received rather than being blamed for a delivery
    /// problem on the platform's side.
    /// </summary>
    Task<ApiResponse<StorefrontEnquiryResponse>> SubmitAsync(
        Guid businessId,
        CreateStorefrontEnquiryRequest request,
        CancellationToken cancellationToken = default);
}
