using Microsoft.Extensions.AI;
using Tandem.Git;

namespace Tandem.Delivery;

public sealed class DeliveryParticipantsFactory(
    AgentFactory agentFactory,
    Func<string, IChatClient> chatClients,
    Func<string, DeliveryAgentProfile> profileResolver,
    DeliveryDiffAcquisition diffAcquisition,
    WorkspacePreparation workspacePreparation,
    GitProcess git,
    AgentCapability<DeliveryState> askPlanner,
    AgentCapability<DeliveryState> submitReport,
    AgentCapability<DeliveryState> writeCheckpoint
)
{
    public DeliveryParticipants Create()
    {
        var agents = new DeliveryAgentFactory(
            agentFactory,
            chatClients,
            profileResolver,
            askPlanner,
            submitReport,
            writeCheckpoint
        );
        var complete = new CompleteRunTransition();
        var failed = new FailRunTransition();

        return new DeliveryParticipants(
            new PrepareWorkspaceStage(new PrepareWorkspaceBlock(workspacePreparation)),
            ExecutorAgent.Create(agents),
            PlannerAgent.Create(agents),
            new CaptureCandidateStage(new CaptureCandidateBlock(git)),
            new VerificationStage(new VerificationBlock(git)),
            ReviewerAgent.Create(agents, diffAcquisition),
            PipelineNodes.Complete<DeliveryState>(
                BlockIds.Complete,
                complete.Execute,
                OutcomeKinds.RunReady,
                "Run ready"
            ),
            PipelineNodes.Failed<DeliveryState>(
                BlockIds.Failed,
                failed.Execute,
                OutcomeKinds.RunFailed,
                failed.Summarize
            ),
            PipelineNodes.WaitFor<DeliveryState, HumanQuestion, HumanAnswer>(
                "HumanInput",
                HumanInteraction.BuildQuestion,
                HumanInteraction.ApplyAnswer
            )
        );
    }
}
