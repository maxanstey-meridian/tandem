using Microsoft.Extensions.AI;
using Tandem.Domain;
using Tandem.Infrastructure.Blocks;
using Tandem.Infrastructure.Lifecycle;
using Tandem.Infrastructure.Projection;

namespace Tandem.Infrastructure.Composition;

public sealed class DeliveryStepsFactory(
    string tandemHome,
    Func<string, IChatClient> chatClientFactory,
    Func<string, ResolvedProfile> profileResolver,
    string? tandemExePath = null
)
{
    public DeliverySteps Create(PipelineBuildContext context)
    {
        var executor = CreateAgentBlock(
            BlockIds.Executor,
            "implementation",
            DeliveryComposition.ExecutorInstructions,
            DeliveryComposition.LifecycleToolsFor(BlockIds.Executor),
            context,
            toolInterceptor: DeliveryComposition.CreateMutationGate(),
            turnPolicy: DeliveryComposition.CreateExecutorTurnPolicy(),
            sessionPolicy: ExecutorPolicies.ContinueWorkingSession,
            teardownPolicy: ExecutorPolicies.ReleaseSessionAfterAcceptedReport
        );
        var planner = CreateAgentBlock(
            BlockIds.Planner,
            "planning",
            DeliveryComposition.PlannerInstructions,
            DeliveryComposition.LifecycleToolsFor(BlockIds.Planner),
            context,
            PlannerDecisionPolicy.Parse,
            structuredOutputAcceptance: DeliveryComposition.CreatePlannerGroundingPolicy(),
            structuredOutputCorrectionRequiredToolName: "file_access_read",
            configureChatOptions: DeliveryComposition.ConfigurePlannerChatOptions,
            sessionPolicy: PlannerPolicies.ContinueConsultation
        );
        var reviewer = CreateAgentBlock(
            BlockIds.Reviewer,
            "review",
            DeliveryComposition.ReviewerInstructions,
            DeliveryComposition.LifecycleToolsFor(BlockIds.Reviewer),
            context,
            ReviewDecisionPolicy.Parse,
            messageAugmentation: DeliveryComposition.CreateDiffAugmentation(),
            structuredOutputAcceptance: DeliveryComposition.CreateReviewerGroundingPolicy(),
            structuredOutputCorrectionRequiredToolName: "file_access_read",
            configureChatOptions: DeliveryComposition.ConfigureReviewerChatOptions,
            sessionPolicy: ReviewerPolicies.StartFreshForEachCandidate,
            teardownPolicy: ReviewerPolicies.TeardownAfterDecision
        );

        return new DeliverySteps(
            new PrepareWorkspaceStage(new PrepareWorkspaceBlock()),
            new ExecutorAgent(executor),
            new PlannerAgent(planner),
            new CaptureCandidateStage(new CaptureCandidateBlock()),
            new VerificationStage(
                new VerificationBlock(context.ExecutionObserver as ICommandOutputObserver)
            ),
            new ReviewerAgent(reviewer),
            new CompleteRunStage(new CompleteBlock()),
            new FailRunStage(new FailedBlock()),
            new HumanQuestionStage(context.ExecutionObserver),
            new HumanInputPort(),
            new ApplyHumanAnswerStage(context.ExecutionObserver)
        );
    }

    private AgentBlock<DeliveryState> CreateAgentBlock(
        string blockId,
        string profileName,
        string instructions,
        IReadOnlyList<string> lifecycleTools,
        PipelineBuildContext context,
        StructuredOutputParser<DeliveryState>? structuredOutput = null,
        ToolInterceptor<DeliveryState>? toolInterceptor = null,
        MessageAugmentation<DeliveryState>? messageAugmentation = null,
        AgentTurnPolicy<DeliveryState>? turnPolicy = null,
        StructuredOutputAcceptancePolicy<DeliveryState>? structuredOutputAcceptance = null,
        string? structuredOutputCorrectionRequiredToolName = null,
        Action<ChatOptions>? configureChatOptions = null,
        AgentSessionPolicy<DeliveryState>? sessionPolicy = null,
        AgentProfilePolicy<DeliveryState>? profilePolicy = null,
        AgentTeardownPolicy<DeliveryState>? teardownPolicy = null
    )
    {
        var profile = profileResolver(profileName);
        var checkpoint = DeliveryComposition.OwnsCheckpointPolicy(blockId)
            ? new CheckpointPolicy<DeliveryState>(
                profile.ContextWindowTokens,
                profile.MaxOutputTokens,
                profile.CheckpointAtPercent,
                "write_checkpoint",
                OutcomeKinds.CheckpointWritten,
                DeliveryComposition.CheckpointOnlyInstructions,
                DeliveryComposition.BuildCheckpointUserMessage,
                (state, _, payload) => state with { CheckpointPayload = payload }
            )
            : null;

        var config = new AgentBlockConfig<DeliveryState>(
            blockId,
            profileName,
            instructions,
            lifecycleTools,
            blockId switch
            {
                BlockIds.Planner => DeliveryComposition.BuildPlannerMessage,
                BlockIds.Reviewer => DeliveryComposition.BuildReviewerMessage,
                _ => DeliveryComposition.BuildExecutorMessage,
            },
            state => state.WorkspacePath,
            state => DeliveryComposition.AllowsWorkspaceMutation(blockId, state),
            structuredOutput,
            checkpoint,
            messageAugmentation,
            turnPolicy,
            structuredOutputAcceptance,
            structuredOutputCorrectionRequiredToolName,
            blockId == BlockIds.Executor
                ? (state, kind, payload) =>
                    kind == OutcomeKinds.ReportSubmitted
                        ? state with
                        {
                            ImplementationReport = payload,
                        }
                        : state
                : null,
            LifecycleActionSetIdentity: lifecycleTools.Count > 0 || checkpoint is not null
                ? DeliveryLifecycleActions.Identity
                : null,
            SessionPolicy: sessionPolicy,
            ProfilePolicy: profilePolicy,
            TeardownPolicy: teardownPolicy
        );

        return new AgentBlock<DeliveryState>(
            config,
            chatClientFactory(profileName),
            tandemHome,
            tandemExePath,
            context.AgentUpdate,
            toolInterceptor,
            configureChatOptions,
            chatClientFactory
        );
    }
}
