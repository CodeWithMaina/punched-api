namespace PunchedApi.Application.Notifications;

/// <summary>
/// The notification type registry — authoritative in-process registry of every
/// notification type the platform may produce. Code, not a table: adding a type
/// is one entry here plus one template entry in <see cref="NotificationTemplates"/>
/// and zero migrations. The delivery worker and all producers resolve types
/// through <see cref="Get"/>.
/// </summary>
public static class NotificationTypes
{
    private static readonly string[] Channels =
    {
        NotificationChannel.InApp,
        NotificationChannel.Email,
        NotificationChannel.Sms,
        NotificationChannel.Push
    };
    private static readonly string[] InAppOnly = { NotificationChannel.InApp };

    /// <summary>
    /// Email only. The verification code is what proves control of an address, so
    /// it must not require prior verification and must never be persisted into the
    /// in-app inbox payload or rendered into SMS/push (defect register D21 — a
    /// plain-text code must not leak into external channels or analytics).
    /// </summary>
    private static readonly string[] SecurityChannels = { NotificationChannel.Email };

    /// <summary>
    /// In-app, email and push — used where the recipient is the business's own
    /// operator and the message is an operational alert their storefront sends.
    ///
    /// <para>Push is declared because the registry's own invariant is that any
    /// email-bearing transactional type also offers push (see
    /// <c>Phase8NotificationChannelsTests</c>). It is inert in a deployment that
    /// registers no push channel, so declaring it cannot deliver anything.
    /// SMS is deliberately excluded: it is opt-in and cost-bearing, and an
    /// enquiry alert must never incur a cost the business did not choose.</para>
    /// </summary>
    private static readonly string[] OperatorAlertChannels =
    {
        NotificationChannel.InApp,
        NotificationChannel.Email,
        NotificationChannel.Push
    };

    /// <summary>All supported transactional notification types.</summary>
    public static readonly IReadOnlyList<NotificationTypeDef> All = new NotificationTypeDef[]
    {
        // ── Appointment ─────────────────────────────────────────────────────
        new("appointment.booked", NotificationCategory.Appointment, Channels)
        {
            PayloadSchema = Schema(Required("businessName", "scheduledAt"), "appointmentId")
        },
        new("appointment.cancelled", NotificationCategory.Appointment, Channels)
        {
            PayloadSchema = Schema(Required("businessName", "scheduledAt"), "appointmentId")
        },
        new("appointment.reschedule_requested", NotificationCategory.Appointment, Channels)
        {
            PayloadSchema = Schema(Required("businessName", "scheduledAt"), "appointmentId")
        },
        new("appointment.reschedule_approved", NotificationCategory.Appointment, Channels)
        {
            PayloadSchema = Schema(Required("businessName", "scheduledAt"), "appointmentId")
        },
        new("appointment.reschedule_rejected", NotificationCategory.Appointment, Channels)
        {
            PayloadSchema = Schema(Required("businessName", "scheduledAt"), "appointmentId")
        },
        new("appointment.reminder", NotificationCategory.Appointment, Channels)
        {
            PayloadSchema = Schema(Required("businessName", "scheduledAt"), "appointmentId")
        },
        new("appointment.completed", NotificationCategory.Appointment, Channels)
        {
            PayloadSchema = Schema(Required("businessName", "scheduledAt"), "appointmentId")
        },
        new("appointment.no_show_notice", NotificationCategory.Appointment, Channels)
        {
            PayloadSchema = Schema(Required("businessName", "scheduledAt"), "appointmentId")
        },
        new("appointment.reschedule_withdrawn", NotificationCategory.Appointment, Channels)
        {
            PayloadSchema = Schema(Required("businessName", "scheduledAt"), "appointmentId")
        },
        new("appointment.reschedule_expired", NotificationCategory.Appointment, Channels)
        {
            PayloadSchema = Schema(Required("businessName", "scheduledAt"), "appointmentId")
        },
        new("appointment.staff_assigned", NotificationCategory.Appointment, Channels)
        {
            PayloadSchema = Schema(Required("businessName", "scheduledAt"), "appointmentId")
        },

        // ── Commissions ─────────────────────────────────────────────────────
        // The staff member is the recipient of every commission notification;
        // these are operational, not marketing, so they respect the same
        // preference resolution as everything else.
        new("commission.approved", NotificationCategory.System, Channels)
        {
            PayloadSchema = Schema(Required("businessName", "amount"), "appointmentId")
        },
        new("commission.adjusted", NotificationCategory.System, Channels)
        {
            PayloadSchema = Schema(Required("businessName", "amount", "reason"), "appointmentId")
        },
        new("commission.payout_recorded", NotificationCategory.System, Channels)
        {
            PayloadSchema = Schema(Required("businessName", "amount", "reference"), "appointmentId")
        },

        // ── Loyalty ─────────────────────────────────────────────────────────
        new("loyalty.stamp_received", NotificationCategory.Loyalty, Channels)
        {
            PayloadSchema = Schema(Required("businessName", "stamps"), "appointmentId")
        },
        new("loyalty.goal_reached", NotificationCategory.Loyalty, Channels)
        {
            PayloadSchema = Schema(Required("businessName", "stamps"))
        },
        new("loyalty.reward_ready", NotificationCategory.Loyalty, Channels)
        {
            PayloadSchema = Schema(Required("businessName"), "rewardName")
        },
        new("loyalty.reward_fulfilled", NotificationCategory.Loyalty, Channels)
        {
            PayloadSchema = Schema(Required("businessName", "rewardName"))
        },
        new("loyalty.reward_cancelled", NotificationCategory.Loyalty, Channels)
        {
            PayloadSchema = Schema(Required("businessName", "rewardName"))
        },
        new("loyalty.card_corrected", NotificationCategory.Loyalty, Channels)
        {
            PayloadSchema = Schema(Required("businessName"), "reason")
        },
        new("loyalty.stamp_expired", NotificationCategory.Loyalty, Channels)
        {
            PayloadSchema = Schema(Required("businessName"))
        },
        new("loyalty.win_back_nudge", NotificationCategory.Loyalty, InAppOnly)
        {
            PayloadSchema = Schema(Required("businessName", "stamps"))
        },
        new("loyalty.burn", NotificationCategory.Loyalty, Channels)
        {
            PayloadSchema = Schema(Required("businessName", "rewardName"))
        },
        new("stamp_awarded", NotificationCategory.Loyalty, Channels)
        {
            PayloadSchema = Schema(Required("businessName", "stamps"), "cardName")
        },
        new("balance_changed", NotificationCategory.Loyalty, Channels)
        {
            PayloadSchema = Schema(Required("businessName"), "balance")
        },
        // Urgent retries (10s → 60s) because the code expires; still bounded by
        // the same attempt ceiling as every other type.
        new("security.verification_code", NotificationCategory.Security, SecurityChannels)
        {
            IsSecurity = true,
            RetryAttempts = 3,
            InitialRetryDelay = TimeSpan.FromSeconds(10),
            MaxRetryDelay = TimeSpan.FromMinutes(1),
            PayloadSchema = Schema(Required("code"), "expiresInMinutes")
        },

        // ── Payment ──────────────────────────────────────────────────────────
        // payment.success producer is out of scope (phase 1 decision); type is
        // registered so templates/ledger references can resolve. payment.failed
        // and payment.reversed are authoritative producer-backed types.
        new("payment.success", NotificationCategory.Payment, Channels)
        {
            PayloadSchema = Schema(Required("businessName", "amount"))
        },
        new("payment.failed", NotificationCategory.Payment, Channels)
        {
            PayloadSchema = Schema(Required("businessName", "amount", "reason"), "appointmentId")
        },
        new("payment.reversed", NotificationCategory.Payment, Channels)
        {
            PayloadSchema = Schema(Required("businessName", "amount", "reason"), "appointmentId")
        },
        // Manual M-PESA. `payment.submitted` goes to the business's own operator (it is
        // their queue), so it uses the operator-alert channel set: in-app always, email
        // and push where configured, and never SMS — an alert about a payment must not
        // incur a cost the business did not choose.
        new("payment.submitted", NotificationCategory.Payment, OperatorAlertChannels)
        {
            PayloadSchema = Schema(
                Required("businessName", "amount"), "customerName", "reference", "transactionCode")
        },
        new("payment.verified", NotificationCategory.Payment, Channels)
        {
            PayloadSchema = Schema(Required("businessName", "amount"), "appointmentId")
        },
        new("payment.rejected", NotificationCategory.Payment, Channels)
        {
            PayloadSchema = Schema(Required("businessName", "amount", "reason"), "appointmentId")
        },
        new("appointment.follow_up", NotificationCategory.Appointment, Channels)
        {
            PayloadSchema = Schema(Required("businessName", "scheduledAt"), "appointmentId")
        },
        new("subscription.expiring", NotificationCategory.System, Channels)
        {
            PayloadSchema = Schema(Required("businessName", "renewalDate", "billingUrl"))
        },
        new("customer.lapsed", NotificationCategory.System, Channels)
        {
            PayloadSchema = Schema(Required("businessName"), "customerName", "daysInactive")
        },

        // ── Storefront ───────────────────────────────────────────────────────
        // An anonymous visitor's enquiry from a business's public page. The
        // recipient is the business owner; the visitor never gets a row, because
        // the platform holds no identity for them and no consent to message them.
        //
        // In-app always, plus email when the deployment has a sender configured
        // (`Email.Enabled` + `Email.FromAddress`) and the owner's SYSTEM/email
        // preference is on — both are the existing, configurable layers, so no
        // second switch is introduced here. With email unconfigured the channel
        // fails permanently at delivery, which the outbox records without
        // retrying; the in-app row the owner reads is written first and is
        // unaffected, so a persisted enquiry is never lost to a delivery fault.
        new("storefront.enquiry", NotificationCategory.System, OperatorAlertChannels)
        {
            PayloadSchema = Schema(Required("businessName", "topic"), "visitorName", "reference")
        },
    };

    private static readonly IReadOnlyDictionary<string, NotificationTypeDef> ByName =
        All.ToDictionary(def => def.Name, StringComparer.Ordinal);

    /// <summary>
    /// Looks up a registered type. Throws for an unregistered type instead of
    /// rendering caller-supplied strings (architecture doc §14.4).
    /// </summary>
    /// <exception cref="ArgumentException">The type is not registered.</exception>
    public static NotificationTypeDef Get(string type)
    {
        if (string.IsNullOrEmpty(type) || !ByName.TryGetValue(type, out var def))
        {
            throw new ArgumentException(
                $"Unknown notification type '{type}'. Register it in {nameof(NotificationTypes)}.{nameof(All)}.",
                nameof(type));
        }

        return def;
    }

    /// <summary>Non-throwing lookup used by adapters that must stay tolerant.</summary>
    public static bool TryGet(string? type, out NotificationTypeDef definition)
    {
        if (!string.IsNullOrEmpty(type) && ByName.TryGetValue(type!, out var def))
        {
            definition = def;
            return true;
        }

        definition = null!;
        return false;
    }

    private static string[] Required(params string[] keys) => keys;

    private static NotificationPayloadSchema Schema(string[] required, params string[] optional) => new()
    {
        Required = new HashSet<string>(required, StringComparer.Ordinal),
        Optional = new HashSet<string>(optional, StringComparer.Ordinal)
    };
}

