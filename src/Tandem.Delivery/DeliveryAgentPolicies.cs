using Tandem.Domain;

namespace Tandem.Delivery;

internal static class ExecutorPolicies
{
    public static AgentSessionDecision ContinueWorkingSession(PipelineMessage<DeliveryState> _) =>
        new(AgentSessionAction.Continue, "Retain implementation context across delivery loops.");

    public static AgentTeardownDecision ReleaseSessionAfterAcceptedReport(
        PipelineMessage<DeliveryState> _,
        BlockOutcome outcome
    ) =>
        outcome.Kind == OutcomeKinds.ReportSubmitted
            ? new(true, true, "The implementation report closes this working session.")
            : AgentTeardownDecision.None("The executor still owns active delivery context.");
}

internal static class PlannerPolicies
{
    public static AgentSessionDecision ContinueConsultation(PipelineMessage<DeliveryState> _) =>
        new(AgentSessionAction.Continue, "Retain constraints across related consultations.");
}

internal static class ReviewerPolicies
{
    public static AgentSessionDecision StartFreshForEachCandidate(
        PipelineMessage<DeliveryState> _
    ) => new(AgentSessionAction.Reset, "Review each captured candidate independently.");

    public static AgentTeardownDecision TeardownAfterDecision(
        PipelineMessage<DeliveryState> _,
        BlockOutcome outcome
    ) => new(true, true, $"Reviewer decision '{outcome.Kind}' is complete.");
}
