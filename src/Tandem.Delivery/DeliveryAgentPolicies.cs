using Tandem.Domain;

namespace Tandem.Delivery;

internal static class ExecutorPolicies
{
    public static AgentSessionDecision ContinueWorkingSession(DeliveryState _) =>
        new(AgentSessionAction.Continue, "Retain implementation context across delivery loops.");

    public static AgentConversationDecision RetainUntilAcceptedReport(
        PipelineMessage<DeliveryState> message,
        BlockOutcome _
    ) =>
        message.State.LastExecutorAction == ExecutorAction.ReportSubmitted
            ? new(AgentConversationRetention.Discard, "The report closes this conversation.")
            : new(AgentConversationRetention.Retain, "The delivery conversation remains active.");
}

internal static class PlannerPolicies
{
    public static AgentSessionDecision ContinueConsultation(DeliveryState _) =>
        new(AgentSessionAction.Continue, "Retain constraints across related consultations.");
}

internal static class ReviewerPolicies
{
    public static AgentSessionDecision StartFreshForEachCandidate(DeliveryState _) =>
        new(AgentSessionAction.Reset, "Review each captured candidate independently.");

    public static AgentConversationDecision DiscardAfterDecision(
        PipelineMessage<DeliveryState> _,
        BlockOutcome outcome
    ) =>
        new(AgentConversationRetention.Discard, $"Reviewer decision '{outcome.Kind}' is complete.");
}
