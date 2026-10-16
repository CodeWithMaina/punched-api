namespace PunchedApi.Application.Notifications;

/// <summary>
/// One registered notification type. Producers never pass a category or a
/// channel list — they pass <see cref="Name"/> and the registry
/// (<see cref="NotificationTypes"/>) supplies the category, the default channel
/// candidates and the copy.
/// </summary>
/// <param name="Name">Dotted producer-facing type, e.g. <c>appointment.booked</c>.</param>
/// <param name="Category">Preference granularity — one of <see cref="NotificationCategory"/>.</param>
/// <param name="DefaultChannels">Channel candidates before preferences are applied.</param>
public sealed record NotificationTypeDef(
    string Name,
    string Category,
    string[] DefaultChannels)
{
    /// <summary>
    /// Payload contract consumed by producers (validation) and the template
    /// renderer (known token set). Missing required keys render as empty text —
    /// they never fail the notification itself.
    /// </summary>
    public NotificationPayloadSchema PayloadSchema { get; init; } = NotificationPayloadSchema.Empty;

    /// <summary>
    /// SECURITY types alone may bypass user preference opt-outs (R2). The flag
    /// is server-side only; no client can set it.
    /// </summary>
    public bool IsSecurity { get; init; }

    /// <summary>Delivery retry policy consumed by the notification delivery worker.</summary>
    public int RetryAttempts { get; init; } = 3;

    /// <summary>Delay before the first retry.</summary>
    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Upper bound for the exponential backoff between retries.</summary>
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromMinutes(5);

    public string TitleTemplate => NotificationTemplates.Get(Name, NotificationChannel.InApp).Title;
    public string BodyTemplate => NotificationTemplates.Get(Name, NotificationChannel.InApp).Body;
}

/// <summary>
/// Payload contract for one registered type: the keys producers must supply,
/// the keys they may supply, and — globally, via <see cref="SensitiveKeys"/> —
/// the keys no notification payload may ever carry.
/// </summary>
public sealed class NotificationPayloadSchema
{
    public static readonly NotificationPayloadSchema Empty = new();

    /// <summary>
    /// Keys no payload may ever contain, whatever the type. Their presence is a
    /// producer bug; the pipeline strips them before persisting.
    /// </summary>
    public static readonly IReadOnlySet<string> SensitiveKeys =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "password", "pin", "cvv", "cardnumber", "refreshtoken", "secret", "apikey"
        };

    public IReadOnlySet<string> Required { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    public IReadOnlySet<string> Optional { get; init; } = new HashSet<string>(StringComparer.Ordinal);
}

