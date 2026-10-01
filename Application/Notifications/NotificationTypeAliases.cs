namespace PunchedApi.Application.Notifications;

public static class NotificationTypeAliases
{
    private static readonly IReadOnlyDictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["GoalReached"] = "loyalty.goal_reached",
        ["RewardReady"] = "loyalty.reward_ready",
        ["RescheduleRequested"] = "appointment.reschedule_requested",
        ["RescheduleApproved"] = "appointment.reschedule_approved",
        ["RescheduleRejected"] = "appointment.reschedule_rejected",
        ["RewardFulfilled"] = "loyalty.reward_fulfilled",
        ["RewardCancelled"] = "loyalty.reward_cancelled"
    };

    public static string Resolve(string type) => Aliases.TryGetValue(type, out var canonical) ? canonical : type;
}