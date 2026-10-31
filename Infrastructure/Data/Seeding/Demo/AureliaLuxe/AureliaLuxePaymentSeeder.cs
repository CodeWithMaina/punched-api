using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data.Seeding.Demo.AureliaLuxe;

/// <summary>
/// Writes the money side of the atelier's appointments.
///
/// No provider is ever contacted: every payment is an internal record whose
/// amount is computed from the appointment's own service snapshots (never
/// hand-written), so revenue, outstanding balances and the payment dashboard all
/// reconcile with the appointment book. <c>business_payment_configs</c> is left
/// without Daraja credentials, so a retry through the UI cannot raise a real
/// M-PESA request either.
/// </summary>
internal static class AureliaLuxePaymentSeeder
{
    public static (int Payments, int Attempts) Write(
        ApplicationDbContext db,
        AureliaLuxeDemoContext demo,
        List<PendingAppointment> appointments,
        DateOnly today,
        DateTime nowUtc)
    {
        var payments = 0;
        var attempts = 0;
        var sequence = 0;

        foreach (var appointment in appointments)
        {
            var total = appointment.Services.Sum(s => s.Price ?? 0m);
            if (total <= 0m)
            {
                continue;
            }

            var rng = new AureliaDemoRandom(5000 + appointment.Index);
            var method = rng.Chance(0.55) ? PaymentMethod.Mpesa : PaymentMethod.Cash;
            var bookingFee = Math.Min(total, Math.Max(500m, Math.Round(total * 0.30m, 2)));
            var balance = total - bookingFee;
            var isUpcoming = appointment.ScheduledAt > nowUtc;
            var phone = ToMpesaPhone(appointment.Customer.User.PhoneNumber);
            var appointmentId = AureliaLuxeDemoIds.Appointment(appointment.Index);
            var paidAt = appointment.Status == "completed"
                ? appointment.EndAt.AddMinutes(5)
                : appointment.ScheduledAt.AddDays(-1);

            switch (ResolveScenario(rng, appointment.Status, isUpcoming))
            {
                case PaymentScenario.FullPaid:
                    payments++;
                    attempts += AddPayment(db, demo, appointment, appointmentId, ++sequence, rng,
                        PaymentType.FullPayment, method, PaymentStatus.Success, total, paidAt, phone);
                    break;

                case PaymentScenario.DepositAndBalance:
                    payments++;
                    attempts += AddPayment(db, demo, appointment, appointmentId, ++sequence, rng,
                        PaymentType.BookingFee, method, PaymentStatus.Success, bookingFee,
                        appointment.ScheduledAt.AddDays(-2), phone);
                    payments++;
                    attempts += AddPayment(db, demo, appointment, appointmentId, ++sequence, rng,
                        PaymentType.BalancePayment,
                        rng.Chance(0.5) ? PaymentMethod.Mpesa : PaymentMethod.Cash,
                        PaymentStatus.Success, balance, paidAt, phone);
                    break;

                case PaymentScenario.DepositOnly:
                    payments++;
                    attempts += AddPayment(db, demo, appointment, appointmentId, ++sequence, rng,
                        PaymentType.BookingFee, method, PaymentStatus.Success, bookingFee,
                        appointment.ScheduledAt.AddDays(-2), phone);
                    break;

                case PaymentScenario.DepositRefunded:
                    payments++;
                    attempts += AddPayment(db, demo, appointment, appointmentId, ++sequence, rng,
                        PaymentType.BookingFee, method, PaymentStatus.Refunded, bookingFee,
                        appointment.ScheduledAt.AddDays(-3), phone,
                        reversedAt: appointment.ScheduledAt.AddDays(-1),
                        reversalReason: "Appointment cancelled inside the 24-hour window — deposit refunded.");
                    break;

                case PaymentScenario.Failed:
                    payments++;
                    attempts += AddPayment(db, demo, appointment, appointmentId, ++sequence, rng,
                        PaymentType.FullPayment, PaymentMethod.Mpesa, PaymentStatus.Failed, total,
                        paidAt, phone);
                    break;

                case PaymentScenario.AwaitingConfirmation:
                    payments++;
                    attempts += AddPayment(db, demo, appointment, appointmentId, ++sequence, rng,
                        PaymentType.BookingFee, method,
                        method == PaymentMethod.Cash ? PaymentStatus.Created : PaymentStatus.AwaitingCustomer,
                        bookingFee, nowUtc.AddMinutes(-30), phone);
                    break;
            }
        }

        return (payments, attempts);
    }

    private static int AddPayment(
        ApplicationDbContext db,
        AureliaLuxeDemoContext demo,
        PendingAppointment appointment,
        Guid appointmentId,
        int sequence,
        AureliaDemoRandom rng,
        PaymentType type,
        PaymentMethod method,
        PaymentStatus status,
        decimal amount,
        DateTime createdAt,
        string? phone,
        DateTime? reversedAt = null,
        string? reversalReason = null)
    {
        var id = AureliaLuxeDemoIds.Payment(sequence);
        var provider = method == PaymentMethod.Mpesa ? PaymentProviderKind.DarajaStk : PaymentProviderKind.Cash;

        var entity = new Payment
        {
            Id = id,
            BusinessId = demo.BusinessId,
            CustomerId = appointment.Customer.User.Id,
            AppointmentId = appointmentId,
            Type = type,
            Method = method,
            Provider = provider,
            Status = status,
            Amount = amount,
            Currency = "KES",
            Reference = $"PMT-AUR-{sequence:D5}",
            PhoneNumber = method == PaymentMethod.Mpesa ? phone : null,
            CreatedByUserId = method == PaymentMethod.Cash ? appointment.StaffUserId : appointment.Customer.User.Id,
            CreatedAt = createdAt,
        };

        var attempts = 0;

        switch (status)
        {
            case PaymentStatus.Success:
                entity.CompletedAt = createdAt.AddMinutes(2);
                entity.ExternalReference = $"MPESA-AUR-{sequence:D5}";
                if (method == PaymentMethod.Cash)
                {
                    entity.ConfirmedByUserId = appointment.StaffUserId;
                }
                else
                {
                    entity.Attempts.Add(BuildAttempt(sequence, id, provider, "success", createdAt, null));
                    attempts++;
                }
                break;

            case PaymentStatus.Failed:
                entity.FailedAt = createdAt.AddMinutes(1);
                entity.Attempts.Add(BuildAttempt(sequence, id, provider, "failed", createdAt,
                    "1032|Request cancelled by user"));
                attempts++;
                break;

            case PaymentStatus.Refunded:
                entity.CompletedAt = createdAt.AddMinutes(2);
                entity.ExternalReference = $"MPESA-AUR-{sequence:D5}";
                entity.ReversedAt = reversedAt ?? createdAt.AddDays(1);
                entity.ReversalReason = reversalReason;
                if (method == PaymentMethod.Cash)
                {
                    entity.ConfirmedByUserId = appointment.StaffUserId;
                }
                else
                {
                    entity.Attempts.Add(BuildAttempt(sequence, id, provider, "success", createdAt, null));
                    attempts++;
                }
                break;

            case PaymentStatus.AwaitingCustomer:
                entity.Attempts.Add(BuildAttempt(sequence, id, provider, "pending", createdAt, null));
                attempts++;
                break;
        }

        _ = rng;
        db.Payments.Add(entity);
        return attempts;
    }
    private static PaymentAttempt BuildAttempt(
        int sequence,
        Guid paymentId,
        PaymentProviderKind provider,
        string status,
        DateTime createdAt,
        string? error)
    {
        return new PaymentAttempt
        {
            Id = AureliaLuxeDemoIds.PaymentAttempt(sequence, 1),
            PaymentId = paymentId,
            AttemptNumber = 1,
            Provider = provider,
            Status = status,
            CheckoutRequestId = $"ws_CO_AUR{sequence:D9}",
            MerchantRequestId = $"aur-merchant-{sequence:D6}",
            ProviderReference = status == "success" ? $"MPESA-AUR-{sequence:D5}" : null,
            ErrorCode = error?.Split('|')[0],
            ErrorMessage = error?.Split('|').ElementAtOrDefault(1),
            CompletedAt = status == "pending" ? null : createdAt.AddMinutes(2),
            CreatedAt = createdAt,
        };
    }

    private static PaymentScenario ResolveScenario(AureliaDemoRandom rng, string status, bool isUpcoming)
    {
        if (status == "completed")
        {
            var roll = rng.NextDouble();
            return roll switch
            {
                < 0.80 => PaymentScenario.FullPaid,
                < 0.88 => PaymentScenario.DepositAndBalance,
                < 0.93 => PaymentScenario.Unpaid,
                < 0.96 => PaymentScenario.Failed,
                _ => PaymentScenario.DepositRefunded,
            };
        }

        if (status == "no_show")
        {
            return rng.Chance(0.45) ? PaymentScenario.DepositOnly : PaymentScenario.Unpaid;
        }

        if (status == "cancelled")
        {
            return rng.Chance(0.25) ? PaymentScenario.DepositRefunded : PaymentScenario.Unpaid;
        }

        if (!isUpcoming)
        {
            return PaymentScenario.Unpaid;
        }

        if (rng.Chance(0.10))
        {
            return PaymentScenario.AwaitingConfirmation;
        }

        return rng.Chance(0.35) ? PaymentScenario.DepositOnly : PaymentScenario.Unpaid;
    }

    private static string? ToMpesaPhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
        {
            return null;
        }

        var digits = new string(phone.Where(char.IsDigit).ToArray());
        return digits.Length >= 9 ? digits : null;
    }

    private enum PaymentScenario
    {
        Unpaid,
        FullPaid,
        DepositAndBalance,
        DepositOnly,
        DepositRefunded,
        Failed,
        AwaitingConfirmation,
    }
}
