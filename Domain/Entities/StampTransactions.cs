namespace PunchedApi.Domain.Entities;

/// <summary>
/// Constants for the <see cref="StampTransaction.Source"/> column. Kept as a
/// string (like <see cref="StampSource"/>) so new producers can be added without
/// a schema change, while the values stay stable and queryable.
/// </summary>
public static class StampTransactions
{
    /// <summary>Stamps earned from a completed appointment.</summary>
    public const string Appointment = "APPOINTMENT";

    /// <summary>Stamps earned from a completed service.</summary>
    public const string Service = "SERVICE";

    /// <summary>Stamps earned from a successful referral (credited to the referrer).</summary>
    public const string Referral = "REFERRAL";

    /// <summary>Stamps awarded manually by a business/staff member.</summary>
    public const string Manual = "MANUAL";

    /// <summary>Welcome stamps granted automatically on enrollment.</summary>
    public const string Enrollment = "ENROLLMENT";

    /// <summary>Stamps consumed by redeeming a reward entitlement.</summary>
    public const string Redemption = "REDEMPTION";

    /// <summary>Owner-initiated correction (positive or negative adjustment).</summary>
    public const string Adjustment = "ADJUSTMENT";

    /// <summary>Safely converts a legacy <see cref="StampSource"/> value to a transaction source.</summary>
    public static string FromLegacyStampSource(string? source) => source switch
    {
        StampSource.Enrollment => Enrollment,
        StampSource.Scan => Manual,
        _ => Manual
    };

    /// <summary>Maps an <see cref="EarningSource"/> to its transaction source constant.</summary>
    public static string FromEarningSource(EarningSource source) => source switch
    {
        EarningSource.Appointment => Appointment,
        EarningSource.Service => Service,
        EarningSource.Referral => Referral,
        _ => Manual
    };
}
