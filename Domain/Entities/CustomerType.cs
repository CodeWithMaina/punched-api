namespace PunchedApi.Domain.Entities;

/// <summary>
/// How a person came to belong to a business — the customer-relationship
/// counterpart of the enrollment row.
///
/// <para><b>Guest</b> — the visitor booked without an account: they were resolved
/// or created by an account-free flow, hold no password they know, and exist at the
/// business only because of the booking that brought them there.</para>
///
/// <para><b>Registered</b> — the person holds (or later obtained) a real Punched
/// account: they enrolled with credentials, scanned a QR, joined a program, were
/// added by the business, or booked while signed in.</para>
///
/// <para>The distinction lives on <see cref="CustomerBusinessEnrollment"/>, not on
/// the identity: the same person can be a Guest at one business and Registered at
/// another. A relationship is only ever <b>upgraded</b> Guest → Registered; it never
/// silently falls back, so a real account is never demoted to a guest.</para>
/// </summary>
public enum CustomerType
{
    /// <summary>Account-free relationship created by a guest booking.</summary>
    Guest = 1,

    /// <summary>A relationship held by a real Punched account.</summary>
    Registered = 2
}
