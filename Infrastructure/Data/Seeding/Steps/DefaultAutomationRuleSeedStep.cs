using Microsoft.EntityFrameworkCore;
using PunchedApi.Domain.Entities;

namespace PunchedApi.Infrastructure.Data.Seeding.Steps;

/// <summary>
/// Seeds per-business default automation rules (Phase 3). All defaults are
/// created disabled (Enabled = false) so they never fire until a business
/// explicitly activates them. Idempotent — re-running creates nothing new.
/// </summary>
public sealed class DefaultAutomationRuleSeedStep : ISeedStep
{
    public string Name => "DefaultAutomationRules";

    public async Task ExecuteAsync(SeedExecutionContext context, CancellationToken cancellationToken)
    {
        var businessIds = context.BusinessesByKey.Values.Select(b => b.Id).ToList();
        var existing = await context.Db.AutomationRules
            .Where(r => businessIds.Contains(r.BusinessId))
            .ToListAsync(cancellationToken);

        var created = 0;
        foreach (var business in context.BusinessesByKey.Values)
        {
            var businessRules = existing.Where(r => r.BusinessId == business.Id).ToList();

            if (!businessRules.Any(r => r.Trigger == "appointment.created" && r.Name == "Booking confirmation"))
            {
                context.Db.AutomationRules.Add(new AutomationRule
                {
                    Id = DeterministicSeed.GuidFor("default-rule", $"{business.Id}:booking-confirmation"),
                    BusinessId = business.Id,
                    Name = "Booking confirmation",
                    Description = "Sends a confirmation to the customer when an appointment is booked.",
                    Trigger = "appointment.created",
                    ConditionsJson = "[]",
                    ActionsJson = "[{\"type\":\"send_notification\",\"payload\":{\"type\":\"appointment.booked\"}}]",
                    RecipientsJson = "[{\"category\":\"customer\",\"refId\":\"$customer_id\"}]",
                    Enabled = false,
                    Version = 1,
                    DailyActionCap = null,
                    FrequencyCapMinutes = 1440,
                    QuietHoursStart = null,
                    QuietHoursEnd = null,
                    CreatedAt = business.CreatedAt,
                    UpdatedAt = business.CreatedAt
                });
                created++;
            }

            if (!businessRules.Any(r => r.Trigger == "time.before_appointment" && r.Name == "Appointment reminders"))
            {
                context.Db.AutomationRules.Add(new AutomationRule
                {
                    Id = DeterministicSeed.GuidFor("default-rule", $"{business.Id}:reminders"),
                    BusinessId = business.Id,
                    Name = "Appointment reminders",
                    Description = "Sends reminders before upcoming appointments (T-24h and T-1h by policy).",
                    Trigger = "time.before_appointment",
                    ConditionsJson = "[]",
                    ActionsJson = "[{\"type\":\"send_notification\",\"payload\":{\"type\":\"appointment.reminder\"}}]",
                    RecipientsJson = "[{\"category\":\"customer\",\"refId\":\"$customer_id\"}]",
                    Enabled = false,
                    Version = 1,
                    DailyActionCap = null,
                    FrequencyCapMinutes = 1440,
                    QuietHoursStart = null,
                    QuietHoursEnd = null,
                    CreatedAt = business.CreatedAt,
                    UpdatedAt = business.CreatedAt
                });
                created++;
            }

            // Appointment follow-up — fires after an appointment completes, encouraging
            // re-booking and feedback. Disabled by default with daily cap + quiet hours.
            if (!businessRules.Any(r => r.Trigger == "appointment.completed" && r.Name == "Appointment follow-up"))
            {
                context.Db.AutomationRules.Add(new AutomationRule
                {
                    Id = DeterministicSeed.GuidFor("default-rule", $"{business.Id}:appointment-follow-up"),
                    BusinessId = business.Id,
                    Name = "Appointment follow-up",
                    Description = "Sends a follow-up message after a completed appointment to encourage re-booking.",
                    Trigger = "appointment.completed",
                    ConditionsJson = "[]",
                    ActionsJson = "[{\"type\":\"send_notification\",\"payload\":{\"type\":\"appointment.follow_up\"}}]",
                    RecipientsJson = "[{\"category\":\"customer\",\"refId\":\"$customer_id\"}]",
                    Enabled = false,
                    Version = 1,
                    DailyActionCap = 5,
                    FrequencyCapMinutes = 1440,
                    QuietHoursStart = new TimeSpan(20, 0, 0),
                    QuietHoursEnd = new TimeSpan(8, 0, 0),
                    CreatedAt = business.CreatedAt,
                    UpdatedAt = business.CreatedAt
                });
                created++;
            }

            // No-show notice — fires when a customer misses an appointment without
            // cancelling. Disabled by default with daily cap + quiet hours.
            if (!businessRules.Any(r => r.Trigger == "appointment.no_show" && r.Name == "No-show notice"))
            {
                context.Db.AutomationRules.Add(new AutomationRule
                {
                    Id = DeterministicSeed.GuidFor("default-rule", $"{business.Id}:no-show-notice"),
                    BusinessId = business.Id,
                    Name = "No-show notice",
                    Description = "Notifies the customer when they miss an appointment and invites them to re-book.",
                    Trigger = "appointment.no_show",
                    ConditionsJson = "[]",
                    ActionsJson = "[{\"type\":\"send_notification\",\"payload\":{\"type\":\"appointment.no_show_notice\"}}]",
                    RecipientsJson = "[{\"category\":\"customer\",\"refId\":\"$customer_id\"}]",
                    Enabled = false,
                    Version = 1,
                    DailyActionCap = 3,
                    FrequencyCapMinutes = 10080,
                    QuietHoursStart = new TimeSpan(21, 0, 0),
                    QuietHoursEnd = new TimeSpan(7, 0, 0),
                    CreatedAt = business.CreatedAt,
                    UpdatedAt = business.CreatedAt
                });
                created++;
            }

            // Reward-ready — fires when a customer has earned enough stamps for a reward.
            // Disabled by default with daily cap + quiet hours.
            if (!businessRules.Any(r => r.Trigger == "loyalty.reward_ready" && r.Name == "Reward ready"))
            {
                context.Db.AutomationRules.Add(new AutomationRule
                {
                    Id = DeterministicSeed.GuidFor("default-rule", $"{business.Id}:reward-ready"),
                    BusinessId = business.Id,
                    Name = "Reward ready",
                    Description = "Notifies the customer when a loyalty reward is ready to redeem.",
                    Trigger = "loyalty.reward_ready",
                    ConditionsJson = "[]",
                    ActionsJson = "[{\"type\":\"send_notification\",\"payload\":{\"type\":\"loyalty.reward_ready\"}}]",
                    RecipientsJson = "[{\"category\":\"customer\",\"refId\":\"$customer_id\"}]",
                    Enabled = false,
                    Version = 1,
                    DailyActionCap = 3,
                    FrequencyCapMinutes = 10080,
                    QuietHoursStart = new TimeSpan(21, 0, 0),
                    QuietHoursEnd = new TimeSpan(7, 0, 0),
                    CreatedAt = business.CreatedAt,
                    UpdatedAt = business.CreatedAt
                });
                created++;
            }
        }
    }
}