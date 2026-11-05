namespace PunchedApi.Application.Notifications;

/// <summary>
/// Answers the one question the dispatch path cannot: can THIS deployment
/// actually deliver on a channel right now? Availability is provider
/// configuration (SMTP / VAPID / Africa's Talking) plus recipient eligibility
/// (verified email address, mobile number), never a user preference.
/// </summary>
/// <remarks>
/// Preferences decide whether a recipient <em>wants</em> a channel; availability
/// decides whether the platform <em>can</em>. SECURITY intents deliberately
/// bypass the former (R2) but must still respect the latter — queuing an email
/// row on a deployment with SMTP switched off can only dead-letter, and an OTP
/// that never lands is worse than a visible "email unavailable" state.
/// </remarks>
public interface INotificationChannelAvailability
{
    /// <summary>
    /// The subset of <paramref name="channels"/> this deployment can deliver for
    /// <paramref name="userId"/>, preserving input order and dropping duplicates.
    /// </summary>
    Task<IReadOnlyList<string>> FilterAvailableAsync(
        Guid userId,
        IEnumerable<string> channels,
        CancellationToken ct = default);
}
