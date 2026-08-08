namespace Tandem.Delivery;

public sealed record DeliveryParticipants(
    PrepareWorkspaceStage PrepareWorkspace,
    AgentDefinition<DeliveryState> Executor,
    AgentDefinition<DeliveryState> Planner,
    CaptureCandidateStage CaptureCandidate,
    VerificationStage Verification,
    AgentDefinition<DeliveryState> Reviewer,
    IPipelineNode<DeliveryState> CompleteRun,
    IPipelineNode<DeliveryState> FailRun,
    PipelineInteraction<DeliveryState, HumanQuestion, HumanAnswer> HumanInput
);
