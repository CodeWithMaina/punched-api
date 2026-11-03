using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using PunchedApi.Application.DTOs;
using PunchedApi.Application.Notifications;
using PunchedApi.Domain.Interfaces;

namespace PunchedApi.Application.Services;

/// <summary>
/// Backs <c>POST /v1/businesses/{businessId}/enquiries</c> — the storefront's
/// contact form.
///
/// <para>The enquiry travels through the notification pipeline rather than a new
/// table: that pipeline already owns "tell this user something happened in this
/// tenant", already enforces recipient/tenant consistency, and already gives the
/// owner a place to read it. A bespoke enquiry entity would duplicate all three
/// and add a migration for no additional capability.</para>
///
/// <para>What is deliberately absent: no customer row is created, no reply is
/// sent, and the visitor is never messaged. The visitor has no account and has
/// given no consent to be contacted by the platform — the business replies on the
/// number the visitor chose to leave, which is the whole point of collecting
/// one.</para>
/// </summary>
public class StorefrontEnquiryService : IStorefrontEnquiryService
{
    /// <summary>Keeps a submitted answer short enough to be a notification payload, not a document.</summary>
    private const int MaxAnswerLength = 120;
    private const int MaxAnswers = 4;
    private const int MaxMessageLength = 500;
    private const int MaxNameLength = 100;

    private static readonly HashSet<string> Topics =
        new(StringComparer.Ordinal) { "services", "event", "booking", "other" };

    /// <summary>
    /// Kenyan mobile numbers only, in any of the shapes a person types them.
    /// Accepting a looser pattern would make the owner's reply path a dead end.
    /// </summary>
    private static readonly Regex KenyanPhone =
        new(@"^(?:\+?254|0)?[17]\d{8}$", RegexOptions.Compiled);

    private readonly IUnitOfWork _unitOfWork;
    private readonly INotificationService _notifications;
    private readonly ILogger<StorefrontEnquiryService> _logger;

    public StorefrontEnquiryService(
        IUnitOfWork unitOfWork,
        INotificationService notifications,
        ILogger<StorefrontEnquiryService> logger)
    {
        _unitOfWork = unitOfWork;
        _notifications = notifications;
        _logger = logger;
    }

    public async Task<ApiResponse<StorefrontEnquiryResponse>> SubmitAsync(
        Guid businessId,
        CreateStorefrontEnquiryRequest request,
        CancellationToken cancellationToken = default)
    {
        if (businessId == Guid.Empty)
            return ApiResponse<StorefrontEnquiryResponse>.Fail("VALIDATION_ERROR", "A business is required.");

        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length < 2 || name.Length > MaxNameLength)
            return ApiResponse<StorefrontEnquiryResponse>.Fail(
                "VALIDATION_ERROR", "Please give a name between 2 and 100 characters.");

        var phone = NormalisePhone(request.PhoneNumber);
        if (phone == null)
            return ApiResponse<StorefrontEnquiryResponse>.Fail(
                "VALIDATION_ERROR", "Use a Kenyan phone number, for example 0712 345 678.");

        var message = (request.Message ?? string.Empty).Trim();
        if (message.Length > MaxMessageLength)
            return ApiResponse<StorefrontEnquiryResponse>.Fail(
                "VALIDATION_ERROR", $"Please keep your message under {MaxMessageLength} characters.");

        var topic = (request.Topic ?? "other").Trim().ToLowerInvariant();
        if (!Topics.Contains(topic)) topic = "other";

        var business = await _unitOfWork.Businesses.FirstOrDefaultAsync(
            b => b.Id == businessId && !b.IsDeleted);
        if (business == null)
            return ApiResponse<StorefrontEnquiryResponse>.Fail("NOT_FOUND", "Business not found.");

        // An enquiry is only meaningful if someone can read it. A business with no
        // owner would silently swallow it, so it is refused rather than accepted.
        if (business.OwnerId is not Guid ownerId)
            return ApiResponse<StorefrontEnquiryResponse>.Fail(
                "NOT_FOUND", "This business cannot receive enquiries right now.");

        var reference = BuildReference();
        var answers = (request.Answers ?? new List<string>())
            .Take(MaxAnswers)
            .Select(answer => Truncate((answer ?? string.Empty).Trim(), MaxAnswerLength))
            .Where(answer => answer.Length > 0)
            .ToList();

        var detail = new List<string>();
        if (answers.Count > 0) detail.Add(string.Join(" · ", answers));
        if (message.Length > 0) detail.Add(message);
        detail.Add($"Reply on {phone}");
        detail.Add($"Ref {reference}");

        try
        {
            // In-app only (see the type registration): the owner reads it in their
            // console. Emailing or texting the *owner* would be an operational
            // alert the platform has not been asked to send, and the payload
            // carries no visitor contact details beyond the reply line above.
            await _notifications.SendAsync(
                new NotificationRequest(
                    Type: "storefront.enquiry",
                    RecipientUserId: ownerId,
                    BusinessId: business.Id,
                    Data: new Dictionary<string, object?>
                    {
                        ["businessName"] = business.Name,
                        ["topic"] = TopicLabel(topic),
                        ["visitorName"] = name,
                        ["reference"] = reference,
                        ["detail"] = string.Join("\n", detail)
                    },
                    IdempotencyKey: $"storefront-enquiry:{business.Id}:{reference}"),
                cancellationToken);
        }
        catch (Exception exception)
        {
            // The pipeline is designed not to throw for provider problems, so this
            // is a genuine fault. The enquiry is still acknowledged: the visitor
            // did nothing wrong, and reporting a failure would invite a retry that
            // duplicates the record.
            _logger.LogError(
                exception,
                "Storefront enquiry {Reference} for business {BusinessId} could not be delivered to the owner.",
                reference,
                business.Id);
        }

        return ApiResponse<StorefrontEnquiryResponse>.Ok(new StorefrontEnquiryResponse
        {
            Reference = reference,
            BusinessName = business.Name,
            ReceivedAt = DateTime.UtcNow
        });
    }

    /// <summary>`ENQ-7F3A2C4D` — short enough to read over the phone, unambiguous in a list.</summary>
    private static string BuildReference()
        => $"ENQ-{Guid.NewGuid():N}"[..12].ToUpperInvariant();

    private static string TopicLabel(string topic) => topic switch
    {
        "services" => "your services",
        "event" => "a group or event",
        "booking" => "a booking",
        _ => "something else"
    };

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max];

    private static string? NormalisePhone(string? input)
    {
        var digits = Regex.Replace((input ?? string.Empty).Trim(), @"[\s\-()]", string.Empty);
        if (!KenyanPhone.IsMatch(digits)) return null;
        return $"254{digits[^9..]}";
    }
}

