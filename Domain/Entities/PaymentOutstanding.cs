using System.Security.Cryptography;

namespace PunchedApi.Domain.Entities;

/// <summary>
/// The one way an "outstanding amount" payment row is built.
///
/// <para>Two flows need exactly the same record: an appointment booked to be paid after
/// the service, and the remainder left behind when a customer pays part of an invoice.
/// Building it here — rather than in two services — is what keeps the shape, the status
/// and the reference format identical, so the Payments module can list either without
/// knowing which produced it.</para>
///
/// <para>The row is deliberately <see cref="PaymentStatus.Created"/> and
/// <see cref="PaymentMethod.Unspecified"/>: it records money OWED, not money received,
/// and it can never be mistaken for a settled payment by any aggregate (every total in
/// the platform reads <see cref="PaymentStatuses.IsSettled"/>).</para>
/// </summary>
public static class PaymentOutstanding
{
    public static Payment Create(
        Guid businessId,
        Guid customerId,
        Guid appointmentId,
        decimal amount,
        Guid? createdByUserId,
        DateTime now) => new()
    {
        Id = Guid.NewGuid(),
        BusinessId = businessId,
        CustomerId = customerId,
        AppointmentId = appointmentId,
        Type = PaymentType.BalancePayment,
        Method = PaymentMethod.Unspecified,
        // No provider call has happened or will happen without a channel; Cash is the
        // enum's neutral default and is replaced the moment the money is collected.
        Provider = PaymentProviderKind.Cash,
        Status = PaymentStatus.Created,
        Amount = decimal.Round(amount, 2, MidpointRounding.AwayFromZero),
        Currency = "KES",
        Reference = PaymentReferences.New(),
        CreatedByUserId = createdByUserId,
        CreatedAt = now
    };
}

/// <summary>
/// Punched-issued payment reference (`PMT-` + 12 hex characters), used as the M-PESA
/// C2B BillRefNumber and the STK AccountReference. One generator, so the booking flow
/// and the payment service cannot produce different-shaped references.
/// </summary>
public static class PaymentReferences
{
    public static string New()
    {
        Span<byte> bytes = stackalloc byte[6];
        RandomNumberGenerator.Fill(bytes);
        return "PMT-" + Convert.ToHexString(bytes);
    }
}
