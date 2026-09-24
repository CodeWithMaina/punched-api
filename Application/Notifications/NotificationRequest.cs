namespace PunchedApi.Application.Notifications;

/// <summary>
/// The one call surface a producer learns: what happened, to whom, in which
/// tenant, with which display data. Categories, channels and preferences are
/// resolved server-side — callers never mention a provider.
/// </summary>
/// <param name="Type">Registered type name, e.g. <c>appointment.booked</c>.</param>
/// <param name="RecipientUserId">The recipient's <c>User.Id</c> (never an email/phone).</param>
/// <param name="BusinessId">Tenant context, or null for a platform-wide notification.</param>
/// <param name="Data">Display data substituted into templates and stored in <c>payload_json</c>.</param>
/// <param name="IdempotencyKey">
/// Natural dedupe key. Phase 2 collapses concurrent duplicates on the ledger's
/// unique index; Phase 1 uses it only for logging.
/// </param>
/// <param name="Force">
/// Server-side only (no client flag exists). Bypasses preferences for
/// <c>SECURITY</c> types exclusively — R2.
/// </param>
public sealed record NotificationRequest(
    string Type,
    Guid RecipientUserId,
    Guid? BusinessId,
    IReadOnlyDictionary<string, object?> Data,
    string? IdempotencyKey = null,
    bool Force = false);

/// <summary>
/// Outcome of accepting a notification intent. Suppression is a normal result,
/// not an error: <see cref="Accepted"/> is false with a
/// <see cref="SuppressReason"/> and no row is written.
/// </summary>
/// <param name="Accepted">True when at least one channel survived preferences.</param>
/// <param name="InboxId">The inbox row written for <c>in_app</c>, when applicable.</param>
/// <param name="Channels">The channels this intent was accepted on.</param>
/// <param name="SuppressReason">Machine-readable reason when nothing survived.</param>
public sealed record NotificationResult(
    bool Accepted,
    Guid? InboxId,
    IReadOnlyList<string> Channels,
    string? SuppressReason = null);
