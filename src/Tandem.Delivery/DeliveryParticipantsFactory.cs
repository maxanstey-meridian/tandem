using Microsoft.Extensions.AI;
using Tandem.Git;

namespace Tandem.Delivery;

public sealed class DeliveryParticipantsFactory(
    Func<string, IChatClient> chatClients,
    Func<string, DeliveryAgentProfile> profileResolver,
    IDeliveryRecordSink records,
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
        var agents = new DeliveryAgentFactory(chatClients, profileResolver, records);
        return new DeliveryParticipants(
            new PrepareWorkspaceStage(workspacePreparation),
            ExecutorAgent.Create(agents, askPlanner, submitReport, writeCheckpoint),
            PlannerAgent.Create(agents),
            new CaptureCandidateStage(git, records),
            new VerificationStage(new VerificationOperation(git, records)),
            ReviewerAgent.Create(agents, diffAcquisition),
            PipelineNodes.Complete(new RunReady()),
            PipelineNodes.Failed(new RunFailed()),
            PipelineNodes.WaitFor<DeliveryState, HumanQuestion, HumanAnswer>(
                "PlannerHumanInput",
                HumanInteraction.BuildPlannerQuestion,
                HumanInteraction.ApplyPlannerAnswer
            ),
            PipelineNodes.WaitFor<DeliveryState, HumanQuestion, HumanAnswer>(
                "ReviewerHumanInput",
                HumanInteraction.BuildReviewerQuestion,
                HumanInteraction.ApplyReviewerAnswer
            )
        );
    }
}
