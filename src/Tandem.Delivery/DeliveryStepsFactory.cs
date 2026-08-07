using Microsoft.Extensions.AI;
using Tandem.Domain;
using Tandem.Git;

namespace Tandem.Delivery;

public sealed class DeliveryStepsFactory(
    AgentRuntime agentRuntime,
    Func<string, IChatClient> chatClients,
    Func<string, ResolvedProfile> profileResolver,
    DeliveryDiffAcquisition diffAcquisition,
    WorkspacePreparation workspacePreparation,
    GitProcess git
)
{
    public DeliverySteps Create(PipelineBuildContext context)
    {
        var executor = CreateAgent(
            BlockIds.Executor,
            "implementation",
            DeliveryPrompts.ExecutorInstructions,
            DeliveryPolicies.LifecycleToolsFor(BlockIds.Executor),
            context,
            toolInterceptor: DeliveryPolicies.CreateMutationGate(),
            turnPolicy: DeliveryPolicies.CreateExecutorTurnPolicy(),
            sessionPolicy: ExecutorPolicies.ContinueWorkingSession,
            teardownPolicy: ExecutorPolicies.ReleaseSessionAfterAcceptedReport
        );
        var planner = CreateAgent(
            BlockIds.Planner,
            "planning",
            DeliveryPrompts.PlannerInstructions,
            DeliveryPolicies.LifecycleToolsFor(BlockIds.Planner),
            context,
            PlannerDecisionPolicy.Parse,
            structuredOutputAcceptance: DeliveryPolicies.CreatePlannerGroundingPolicy(),
            structuredOutputCorrectionRequiredToolName: "file_access_read",
            configureChatOptions: DeliveryPolicies.ConfigurePlannerChatOptions,
            sessionPolicy: PlannerPolicies.ContinueConsultation
        );
        var reviewer = CreateAgent(
            BlockIds.Reviewer,
            "review",
            DeliveryPrompts.ReviewerInstructions,
            DeliveryPolicies.LifecycleToolsFor(BlockIds.Reviewer),
            context,
            ReviewDecisionPolicy.Parse,
            messageAugmentation: DeliveryPolicies.CreateDiffAugmentation(diffAcquisition),
            structuredOutputAcceptance: DeliveryPolicies.CreateReviewerGroundingPolicy(),
            structuredOutputCorrectionRequiredToolName: "file_access_read",
            configureChatOptions: DeliveryPolicies.ConfigureReviewerChatOptions,
            sessionPolicy: ReviewerPolicies.StartFreshForEachCandidate,
            teardownPolicy: ReviewerPolicies.TeardownAfterDecision
        );

        return new DeliverySteps(
            new PrepareWorkspaceStage(new PrepareWorkspaceBlock(workspacePreparation)),
            new ExecutorAgent(executor),
            new PlannerAgent(planner),
            new CaptureCandidateStage(new CaptureCandidateBlock(git)),
            new VerificationStage(
                new VerificationBlock(git, context.ExecutionObserver as ICommandOutputObserver)
            ),
            new ReviewerAgent(reviewer),
            new CompleteRunStage(new CompleteBlock()),
            new FailRunStage(new FailedBlock()),
            new HumanQuestionStage(context.ExecutionObserver),
            new HumanInputPort(),
            new ApplyHumanAnswerStage(context.ExecutionObserver)
        );
    }

    private AgentOperation<DeliveryState> CreateAgent(
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
        var checkpoint = DeliveryPolicies.OwnsCheckpointPolicy(blockId)
            ? new CheckpointPolicy<DeliveryState>(
                profile.ContextWindowTokens,
                profile.MaxOutputTokens,
                profile.CheckpointAtPercent,
                "write_checkpoint",
                OutcomeKinds.CheckpointWritten,
                DeliveryPrompts.CheckpointOnlyInstructions,
                DeliveryPrompts.BuildCheckpointUserMessage,
                (state, _, payload) => state with { CheckpointPayload = payload }
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
            .WithMessage(
                blockId switch
                {
                    BlockIds.Planner => DeliveryPrompts.BuildPlannerMessage,
                    BlockIds.Reviewer => DeliveryPrompts.BuildReviewerMessage,
                    _ => DeliveryPrompts.BuildExecutorMessage,
                }
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

        if (structuredOutput is not null)
        {
            builder.WithStructuredOutput(
                structuredOutput,
                configureChatOptions,
                structuredOutputAcceptance,
                structuredOutputCorrectionRequiredToolName
            );
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
        if (teardownPolicy is not null)
        {
            builder.WithTeardownPolicy(teardownPolicy);
        }
        if (lifecycleTools.Count > 0 || checkpoint is not null)
        {
            builder.WithLifecycleActions(
                DeliveryLifecycleActions.Identity,
                lifecycleTools,
                blockId == BlockIds.Executor
                    ? (state, kind, payload) =>
                        kind == OutcomeKinds.ReportSubmitted
                            ? state with
                            {
                                ImplementationReport = payload,
                            }
                            : state
                    : null
            );
        }
        if (checkpoint is not null)
        {
            builder.WithCheckpoint(checkpoint);
        }

        return builder.Build(context);
    }
}
