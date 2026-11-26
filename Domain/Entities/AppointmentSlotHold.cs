namespace PunchedApi.Domain.Entities;

/// <summary>
/// A temporary claim on one staff member's time, taken while a customer pays the booking fee.
///
/// <para><b>Why this is not an appointment.</b> An appointment is a booking: it belongs to a
/// business, a customer and a resource, it has a lifecycle, and it blocks its interval until it is
/// cancelled, completed or marked no-show. A hold is none of those things — it is the promise of a
/// booking that has not been agreed yet, and it must vanish on its own if the customer walks away.
/// Storing it as an appointment would mean every abandoned payment attempt left a phantom booking
/// in the calendar, the customer's history and the business's reporting.</para>
///
/// <para><b>Expiry is a property of the row, not a job.</b> <see cref="ExpiresAtUtc"/> is written
/// when the hold is created and every read compares it with the current time, so a hold stops
/// blocking availability the instant it lapses — no sweep has to run first. A periodic sweep still
/// marks lapsed rows <see cref="AppointmentSlotHoldStatuses.Expired"/> for reporting and housekeeping,
/// but correctness never depends on it.</para>
/// </summary>
public class AppointmentSlotHold : BaseEntity
{
    public Guid BusinessId { get; set; }

    /// <summary>The bookable resource whose interval is claimed. A hold is always staff-bound.</summary>
    public Guid StaffUserId { get; set; }

    /// <summary>
    /// The customer identity behind the hold, when there already is one. A first-time visitor has
    /// no account yet, so the hold carries the typed details instead and this stays null.
    /// </summary>
    public Guid? CustomerUserId { get; set; }

    /// <summary>Start of the claimed interval (UTC).</summary>
    public DateTime StartsAtUtc { get; set; }

    /// <summary>End of the claimed interval (UTC) — start plus the combined service duration.</summary>
    public DateTime EndsAtUtc { get; set; }

    /// <summary>The requested service ids, in the order the customer chose them.</summary>
    public string ServiceIdsJson { get; set; } = "[]";

    /// <summary>
    /// The requested service ids, read from <see cref="ServiceIdsJson"/>. Not mapped: the column
    /// stays a single JSON value, and callers get the ids without re-implementing the parse.
    /// </summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public Guid[] ServiceIds
    {
        get
        {
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<Guid[]>(ServiceIdsJson) ?? Array.Empty<Guid>();
            }
            catch (System.Text.Json.JsonException)
            {
                return Array.Empty<Guid>();
            }
        }
    }


    /// <summary>Combined duration of the requested services, snapshotted when the hold was taken.</summary>
    public int DurationMinutes { get; set; }

    /// <summary>What the customer typed, so the booking can be created without asking again.</summary>
    public string? CustomerName { get; set; }

    public string? CustomerPhone { get; set; }

    public string? CustomerEmail { get; set; }

    public string? Note { get; set; }

    /// <summary>The payment option the customer chose for this booking.</summary>
    public string? PaymentOption { get; set; }

    /// <summary>The client's booking-intent key, carried through to the appointment.</summary>
    public string? IdempotencyKey { get; set; }

    /// <summary>
    /// When the claim lapses. Every read compares this with now, so an expired hold releases its
    /// interval immediately and can never be converted into a booking.
    /// </summary>
    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>One of <see cref="AppointmentSlotHoldStatuses"/>.</summary>
    public string Status { get; set; } = AppointmentSlotHoldStatuses.Active;

    /// <summary>Set when the hold was converted, so the booking it became is traceable.</summary>
    public Guid? ConvertedAppointmentId { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>The hold lifecycle, as a closed vocabulary.</summary>
public static class AppointmentSlotHoldStatuses
{
    /// <summary>The interval is claimed and still within its window.</summary>
    public const string Active = "active";

    /// <summary>The hold became an appointment.</summary>
    public const string Converted = "converted";

    /// <summary>The customer gave it up (or the flow replaced it).</summary>
    public const string Released = "released";

    /// <summary>The window passed without the booking being completed.</summary>
    public const string Expired = "expired";

    /// <summary>Every status that still blocks the staff member's interval.</summary>
    public static readonly IReadOnlyList<string> Blocking = new[] { Active };
}
