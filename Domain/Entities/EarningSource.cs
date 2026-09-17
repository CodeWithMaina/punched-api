namespace PunchedApi.Domain.Entities;

/// <summary>
/// The qualifying event that can award loyalty stamps for a program.
/// Each source is owned by the module that produces it (Appointments owns
/// <see cref="Appointment"/>, ServiceCatalog owns <see cref="Service"/>, Referrals
/// owns <see cref="Referral"/>); Loyalty only reacts to it.
/// Persisted as an int so the catalog can grow without a schema rewrite.
/// </summary>
public enum EarningSource
{
    /// <summary>A completed appointment (see <see cref="Appointment.Status"/> == "completed").</summary>
    Appointment = 1,

    /// <summary>A completed service within an appointment.</summary>
    Service = 2,

    /// <summary>A successful referral (referee completed their first qualifying appointment).</summary>
    Referral = 3
}
