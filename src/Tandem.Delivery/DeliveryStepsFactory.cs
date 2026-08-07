using Microsoft.Extensions.AI;
using Tandem.Advanced;
using Tandem.Domain;
using Tandem.Git;

namespace Tandem.Delivery;

public sealed class DeliveryStepsFactory(
    AgentRuntime agentRuntime,
    Func<string, IChatClient> chatClients,
    Func<string, ResolvedProfile> profileResolver,
    DeliveryDiffAcquisition diffAcquisition,
    WorkspacePreparation workspacePreparation,
    GitProcess git,
    AgentCapability<DeliveryState> askPlanner,
    AgentCapability<DeliveryState> submitReport,
    AgentCapability<DeliveryState> writeCheckpoint
)
{
    public DeliverySteps Create(PipelineBuildContext context)
    {
        var executor = CreateAgent(
            BlockIds.Executor,
            "implementation",
            DeliveryPrompts.ExecutorInstructions,
            toolInterceptor: DeliveryPolicies.CreateMutationGate(),
            turnPolicy: DeliveryPolicies.CreateExecutorTurnPolicy(),
            sessionPolicy: ExecutorPolicies.ContinueWorkingSession,
            conversationPolicy: ExecutorPolicies.RetainUntilAcceptedReport
        );
        var planner = CreateAgent(
            BlockIds.Planner,
            "planning",
            DeliveryPrompts.PlannerInstructions,
            PlannerDecisionPolicy.Parse,
            structuredOutputAcceptance: DeliveryPolicies.CreatePlannerGroundingPolicy(),
            structuredOutputCorrectionRequiredToolName: "file_access_read",
            sessionPolicy: PlannerPolicies.ContinueConsultation
        );
        var reviewer = CreateAgent(
            BlockIds.Reviewer,
            "review",
            DeliveryPrompts.ReviewerInstructions,
            ReviewDecisionPolicy.Parse,
            messageAugmentation: DeliveryPolicies.CreateDiffAugmentation(diffAcquisition),
            structuredOutputAcceptance: DeliveryPolicies.CreateReviewerGroundingPolicy(),
            structuredOutputCorrectionRequiredToolName: "file_access_read",
            sessionPolicy: ReviewerPolicies.StartFreshForEachCandidate,
            conversationPolicy: ReviewerPolicies.DiscardAfterDecision
        );

        var complete = new CompleteBlock();
        var failed = new FailedBlock();

        return new DeliverySteps(
            new PrepareWorkspaceStage(new PrepareWorkspaceBlock(workspacePreparation)),
            executor,
            planner,
            new CaptureCandidateStage(new CaptureCandidateBlock(git)),
            new VerificationStage(
                new VerificationBlock(git, context.ExecutionObserver as ICommandOutputObserver)
            ),
            reviewer,
            PipelineNodes.Complete<DeliveryState>(
                BlockIds.Complete,
                complete.Execute,
                OutcomeKinds.RunReady,
                "Run ready",
                context.ExecutionObserver
            ),
            PipelineNodes.Failed<DeliveryState>(
                BlockIds.Failed,
                failed.Execute,
                OutcomeKinds.RunFailed,
                failed.Summarize,
                context.ExecutionObserver
            ),
            PipelineNodes.WaitFor<DeliveryState, HumanQuestion, HumanAnswer>(
                "HumanInput",
                HumanInteraction.BuildQuestion,
                HumanInteraction.ApplyAnswer,
                context.ExecutionObserver
            )
        );
    }

    private AgentDefinition<DeliveryState> CreateAgent(
        string blockId,
        string profileName,
        string instructions,
        StructuredOutputParser<DeliveryState>? structuredOutput = null,
        ToolInterceptor<DeliveryState>? toolInterceptor = null,
        MessageAugmentation<DeliveryState>? messageAugmentation = null,
        AgentTurnPolicy<DeliveryState>? turnPolicy = null,
        StructuredOutputAcceptancePolicy<DeliveryState>? structuredOutputAcceptance = null,
        string? structuredOutputCorrectionRequiredToolName = null,
        AgentSessionPolicy<DeliveryState>? sessionPolicy = null,
        AgentProfilePolicy<DeliveryState>? profilePolicy = null,
        AgentConversationPolicy<DeliveryState>? conversationPolicy = null
    )
    {
        var profile = profileResolver(profileName);
        var checkpoint = DeliveryPolicies.OwnsCheckpointPolicy(blockId)
            ? new CheckpointPolicy<DeliveryState>(
                profile.ContextWindowTokens,
                profile.MaxOutputTokens,
                profile.CheckpointAtPercent,
                writeCheckpoint,
                DeliveryPrompts.CheckpointOnlyInstructions,
                DeliveryPrompts.BuildCheckpointUserMessage
            )
            : null;

        var builder = agentRuntime
            .Create<DeliveryState>(
                blockId,
                profileName,
                instructions,
                chatClients(profileName),
                chatClients
            )
            .WithWorkspace(
                state => state.WorkspacePath,
                state => DeliveryPolicies.AllowsWorkspaceMutation(blockId, state),
                toolInterceptor
            )
            .WithSessionPolicy(
                sessionPolicy
                    ?? throw new InvalidOperationException(
                        $"Agent '{blockId}' must supply a session policy."
                    )
            );

        if (blockId == BlockIds.Executor)
        {
            builder.WithCapability(askPlanner);
            builder.WithCapability(submitReport);
        }

        if (blockId == BlockIds.Planner)
        {
            builder.WithMessageFromContext(DeliveryPrompts.BuildPlannerMessage);
        }
        else
        {
            builder.WithMessage(
                blockId == BlockIds.Reviewer
                    ? DeliveryPrompts.BuildReviewerMessage
                    : DeliveryPrompts.BuildExecutorMessage
            );
        }

        if (structuredOutput is not null)
        {
            if (blockId == BlockIds.Planner)
            {
                builder.WithOutput<DeliveryState, PlannerDecision>(
                    structuredOutput,
                    structuredOutputAcceptance,
                    structuredOutputCorrectionRequiredToolName
                );
            }
            else
            {
                builder.WithOutput<DeliveryState, ReviewDecision>(
                    structuredOutput,
                    structuredOutputAcceptance,
                    structuredOutputCorrectionRequiredToolName
                );
            }
        }
        if (messageAugmentation is not null)
        {
            builder.WithMessageAugmentation(messageAugmentation);
        }
        if (turnPolicy is not null)
        {
            builder.WithContinuationPolicy(turnPolicy);
        }
        if (profilePolicy is not null)
        {
            builder.WithProfilePolicy(profilePolicy);
        }
        if (conversationPolicy is not null)
        {
            builder.WithConversationPolicy(conversationPolicy);
        }
        if (checkpoint is not null)
        {
            builder.WithCheckpoint(checkpoint);
        }

        return builder.Build();
    }
}
