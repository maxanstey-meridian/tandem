using Tandem.Advanced;

namespace Tandem.Delivery;

internal static class ExecutorPolicies
{
    public static AgentConversationDecision RetainUntilAcceptedReport(
        AgentMessageContext<DeliveryState> context,
        AgentMessageOutcome _
    ) =>
        context.State.LastExecutorAction == ExecutorAction.ReportSubmitted
            ? new(AgentConversationRetention.Discard)
            : new(AgentConversationRetention.Retain);
}

internal static class ReviewerPolicies
{
    public static AgentConversationDecision DiscardAfterDecision(
        AgentMessageContext<DeliveryState> _,
        AgentMessageOutcome __
    ) => new(AgentConversationRetention.Discard);
}
