using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Tandem.Domain;
using Tandem.Infrastructure.Blocks;
using Tandem.Infrastructure.Projection;

namespace Tandem.Infrastructure.Composition;

public sealed class SimpleV1Composition(
    string tandemHome,
    Func<string, Microsoft.Extensions.AI.IChatClient> chatClientFactory,
    Func<string, ResolvedProfile> profileResolver,
    string? tandemExePath = null
)
{
    public Workflow Build(
        Action<string, Guid, AgentResponseUpdate>? onUpdate = null,
        IBlockExecutionObserver? blockObserver = null
    )
    {
        Executor<PipelineMessage, PipelineMessage> prepare = new PrepareWorkspaceBlock();
        Executor<PipelineMessage, PipelineMessage> executor = CreateAgentBlock(
            BlockIds.Executor,
            "implementation",
            ExecutorInstructions,
            WorkspaceAccess.MutationGated,
            ["ask_planner", "submit_report"],
            onUpdate,
            toolInterceptor: CreateMutationGate(),
            turnPolicy: CreateExecutorTurnPolicy()
        );
        Executor<PipelineMessage, PipelineMessage> planner = CreateAgentBlock(
            BlockIds.Planner,
            "planning",
            PlannerInstructions,
            WorkspaceAccess.ReadOnly,
            [],
            onUpdate,
            PlannerDecisionPolicy.Parse,
            configureChatOptions: ConfigurePlannerChatOptions
        );
        Executor<PipelineMessage, PipelineMessage> captureCandidate = new CaptureCandidateBlock();
        Executor<PipelineMessage, PipelineMessage> verify = new VerificationBlock(
            blockObserver as ICommandOutputObserver
        );
        Executor<PipelineMessage, PipelineMessage> reviewer = CreateAgentBlock(
            BlockIds.Reviewer,
            "review",
            ReviewerInstructions,
            WorkspaceAccess.ReadOnly,
            [],
            onUpdate,
            ReviewDecisionPolicy.Parse,
            messageAugmentation: CreateDiffAugmentation(),
            configureChatOptions: ConfigureReviewerChatOptions
        );
        Executor<PipelineMessage, PipelineMessage> complete = new CompleteBlock();
        Executor<PipelineMessage, PipelineMessage> failed = new FailedBlock();

        // Human-input blocks replace the terminal waiting block.
        Executor<PipelineMessage, HumanQuestion> humanQuestion = new HumanQuestionBlock();
        Executor<HumanAnswer, PipelineMessage> applyHumanAnswer = new ApplyHumanAnswerBlock();
        var humanInputPort = RequestPort.Create<HumanQuestion, HumanAnswer>("HumanInput");

        if (blockObserver is not null)
        {
            prepare = Observe(BlockIds.Prepare, prepare, blockObserver);
            executor = Observe(BlockIds.Executor, executor, blockObserver);
            planner = Observe(BlockIds.Planner, planner, blockObserver);
            captureCandidate = Observe(BlockIds.CaptureCandidate, captureCandidate, blockObserver);
            verify = Observe(BlockIds.Verify, verify, blockObserver);
            reviewer = Observe(BlockIds.Reviewer, reviewer, blockObserver);
            complete = Observe(BlockIds.Complete, complete, blockObserver);
            failed = Observe(BlockIds.Failed, failed, blockObserver);
            humanQuestion = Observe(BlockIds.HumanQuestion, humanQuestion, blockObserver);
            applyHumanAnswer = Observe(BlockIds.ApplyHumanAnswer, applyHumanAnswer, blockObserver);
        }

        var prepareBinding = prepare.BindExecutor();
        var executorBinding = executor.BindExecutor();
        var plannerBinding = planner.BindExecutor();
        var captureBinding = captureCandidate.BindExecutor();
        var verifyBinding = verify.BindExecutor();
        var reviewerBinding = reviewer.BindExecutor();
        var completeBinding = complete.BindExecutor();
        var failedBinding = failed.BindExecutor();
        var humanQuestionBinding = humanQuestion.BindExecutor();
        var humanInputBinding = (ExecutorBinding)humanInputPort;
        var applyHumanAnswerBinding = applyHumanAnswer.BindExecutor();

        var builder = new WorkflowBuilder(prepareBinding).WithName("simple-v1");

        builder = AddOutcomeEdge(
            builder,
            prepareBinding,
            executorBinding,
            OutcomeKinds.WorkspacePrepared
        );
        builder = builder.AddEdge(prepareBinding, failedBinding, CatchAllPrepared());

        builder = AddOutcomeEdge(
            builder,
            executorBinding,
            plannerBinding,
            OutcomeKinds.PlannerRequested
        );
        builder = AddOutcomeEdge(
            builder,
            executorBinding,
            captureBinding,
            OutcomeKinds.ReportSubmitted
        );
        builder = AddOutcomeEdge(
            builder,
            executorBinding,
            executorBinding,
            OutcomeKinds.CheckpointWritten
        );
        builder = builder.AddEdge(executorBinding, failedBinding, CatchAllExecutor());

        // Keep successful planner outcomes on one route. DurableTask batches
        // same-target routes, which would otherwise deliver String[] instead
        // of the PipelineMessage expected by the executor.
        builder = builder.AddEdge<PipelineMessage>(
            plannerBinding,
            executorBinding,
            msg =>
                msg!.LatestOutcome?.Kind == OutcomeKinds.PlannerProceed
                || msg.LatestOutcome?.Kind == OutcomeKinds.PlannerProceedWithConstraints
        );
        builder = AddOutcomeEdge(
            builder,
            plannerBinding,
            humanQuestionBinding,
            OutcomeKinds.PlannerNeedsHuman
        );
        builder = AddOutcomeEdge(builder, plannerBinding, failedBinding, OutcomeKinds.PlannerStop);
        builder = builder.AddEdge(plannerBinding, failedBinding, CatchAllPlanner());

        builder = AddOutcomeEdge(
            builder,
            captureBinding,
            verifyBinding,
            OutcomeKinds.CandidateCaptured,
            HasVerificationCommands
        );
        builder = AddOutcomeEdge(
            builder,
            captureBinding,
            reviewerBinding,
            OutcomeKinds.CandidateCaptured,
            NoVerificationCommands
        );
        builder = builder.AddEdge(captureBinding, failedBinding, CatchAllCandidate());

        builder = AddOutcomeEdge(
            builder,
            verifyBinding,
            verifyBinding,
            OutcomeKinds.CommandPassed,
            HasRemainingCommands
        );
        builder = AddOutcomeEdge(
            builder,
            verifyBinding,
            reviewerBinding,
            OutcomeKinds.CommandPassed,
            AllCommandsComplete
        );
        builder = AddOutcomeEdge(
            builder,
            verifyBinding,
            executorBinding,
            OutcomeKinds.CommandFailed
        );
        builder = builder.AddEdge(verifyBinding, failedBinding, CatchAllVerify());

        builder = AddOutcomeEdge(
            builder,
            reviewerBinding,
            completeBinding,
            OutcomeKinds.ReviewAccepted
        );
        builder = AddOutcomeEdge(
            builder,
            reviewerBinding,
            executorBinding,
            OutcomeKinds.ReviewChangesRequested
        );
        builder = AddOutcomeEdge(
            builder,
            reviewerBinding,
            humanQuestionBinding,
            OutcomeKinds.ReviewNeedsHuman
        );
        builder = builder.AddEdge(reviewerBinding, failedBinding, CatchAllReviewer());

        // Human input: question → request port (suspends) → apply answer →
        // route back to the source decision block.
        builder = builder.AddEdge(humanQuestionBinding, humanInputBinding);
        builder = builder.AddEdge(humanInputBinding, applyHumanAnswerBinding);

        // Route the answer back to the originating decision block. The
        // apply-human-answer block's outcome payload carries the source
        // block ID. Combine same-target routes into one predicate to avoid
        // durable batching delivering String[] instead of PipelineMessage.
        builder = builder.AddEdge<PipelineMessage>(
            applyHumanAnswerBinding,
            plannerBinding,
            msg =>
                msg!.LatestOutcome?.Payload.TryGetProperty("sourceBlockId", out var sb) == true
                && sb.ValueKind == System.Text.Json.JsonValueKind.String
                && sb.GetString() == BlockIds.Planner
        );
        builder = builder.AddEdge<PipelineMessage>(
            applyHumanAnswerBinding,
            reviewerBinding,
            msg =>
                msg!.LatestOutcome?.Payload.TryGetProperty("sourceBlockId", out var sb) == true
                && sb.ValueKind == System.Text.Json.JsonValueKind.String
                && sb.GetString() == BlockIds.Reviewer
        );
        builder = builder.AddEdge(applyHumanAnswerBinding, failedBinding, CatchAllApplyAnswer());

        builder = builder.WithOutputFrom(completeBinding, failedBinding);
        return builder.Build();
    }

    private static Executor<TInput, TOutput> Observe<TInput, TOutput>(
        string blockId,
        Executor<TInput, TOutput> executor,
        IBlockExecutionObserver observer
    ) => new ObservedExecutor<TInput, TOutput>(blockId, executor, observer);

    private AgentBlock CreateAgentBlock(
        string blockId,
        string profileName,
        string instructions,
        WorkspaceAccess access,
        IReadOnlyList<string> lifecycleTools,
        Action<string, Guid, AgentResponseUpdate>? onUpdate,
        StructuredOutputParser? structuredOutput = null,
        ToolInterceptor? toolInterceptor = null,
        MessageAugmentation? messageAugmentation = null,
        AgentTurnPolicy? turnPolicy = null,
        Action<ChatOptions>? configureChatOptions = null
    )
    {
        var profile = profileResolver(profileName);
        var checkpoint = new CheckpointPolicy(
            profile.ContextWindowTokens,
            profile.MaxOutputTokens,
            profile.CheckpointAtPercent
        );

        var config = new AgentBlockConfig(
            blockId,
            profileName,
            instructions,
            access,
            lifecycleTools,
            structuredOutput,
            checkpoint,
            messageAugmentation,
            turnPolicy
        );
        return new AgentBlock(
            config,
            chatClientFactory(profileName),
            tandemHome,
            tandemExePath,
            onUpdate,
            toolInterceptor,
            configureChatOptions
        );
    }

    private static void ConfigurePlannerChatOptions(ChatOptions options) =>
        options.ResponseFormat = ChatResponseFormat.ForJsonSchema<PlannerDecision>();

    private static void ConfigureReviewerChatOptions(ChatOptions options) =>
        options.ResponseFormat = ChatResponseFormat.ForJsonSchema<ReviewDecision>();

    private static ToolInterceptor CreateMutationGate() =>
        (ctx, fic, ct) =>
        {
            if (ctx.MutationAuthorized)
            {
                return ValueTask.FromResult<ToolInterceptionResult?>(null);
            }

            var name = fic.Function.Name;
            var isWrite =
                name.StartsWith("file_access_write")
                || name.StartsWith("file_access_replace")
                || name.StartsWith("file_access_delete");

            if (!isWrite)
            {
                return ValueTask.FromResult<ToolInterceptionResult?>(null);
            }

            return ValueTask.FromResult<ToolInterceptionResult?>(
                new ToolInterceptionResult.Blocked(
                    """
                    MUTATION GATE CLOSED: Your edit was NOT applied — no file was changed.
                    Mutation authority is not yet granted. Call ask_planner with your
                    proposed approach and evidence. Reads remain available for gathering
                    evidence. Continue only on proceed or proceed_with_constraints.
                    """
                )
            );
        };

    private static AgentTurnPolicy CreateExecutorTurnPolicy() =>
        new(
            maxContinuationAttempts: 2,
            (observation, _) =>
                ValueTask.FromResult<AgentTurnDirective?>(
                    !observation.Context.MutationAuthorized
                        ? new AgentTurnDirective(
                            """
                            Your previous response was not a lifecycle route. Continue the
                            executor turn by calling ask_planner now with the question you
                            need answered, your proposed approach, and repository evidence.
                            Do not answer with prose; the next action must be the ask_planner
                            tool call.
                            """,
                            RequiredToolName: "ask_planner"
                        )
                        : new AgentTurnDirective(
                            """
                            Your previous response was not a lifecycle route. Continue the
                            implementation and call submit_report when the packet outcomes are
                            ready for verification. Do not treat prose as completion.
                            """,
                            RequiredToolName: "submit_report"
                        )
                )
        );

    private static MessageAugmentation CreateDiffAugmentation() =>
        async (ctx, ct) =>
        {
            if (ctx.CandidateSha is null || string.IsNullOrEmpty(ctx.PinnedBaseSha))
            {
                return null;
            }

            var git = new GitProcess();
            var range = $"{ctx.PinnedBaseSha}..{ctx.CandidateSha}";

            var nameStatusResult = await git.RunAsync(
                ctx.WorkspacePath,
                ["diff", "--name-status", "-z", range],
                ct
            );
            var diffResult = await git.RunAsync(ctx.WorkspacePath, ["diff", "--binary", range], ct);
            var changedFiles = nameStatusResult.Stdout.Replace('\0', '\n');

            return $"""
                Changed files:
                {changedFiles}

                Diff:
                {diffResult.Stdout}
                """;
        };

    private static WorkflowBuilder AddOutcomeEdge(
        WorkflowBuilder builder,
        ExecutorBinding source,
        ExecutorBinding target,
        string outcomeKind,
        Func<PipelineMessage, bool>? extraCondition = null
    )
    {
        return builder.AddEdge<PipelineMessage>(
            source,
            target,
            msg => msg!.LatestOutcome?.Kind == outcomeKind && (extraCondition?.Invoke(msg) ?? true)
        );
    }

    private static Func<PipelineMessage?, bool> CatchAllPrepared() =>
        msg => msg!.LatestOutcome?.Kind != OutcomeKinds.WorkspacePrepared;

    private static Func<PipelineMessage?, bool> CatchAllExecutor() =>
        msg =>
            msg!.LatestOutcome?.Kind != OutcomeKinds.PlannerRequested
            && msg!.LatestOutcome?.Kind != OutcomeKinds.ReportSubmitted
            && msg!.LatestOutcome?.Kind != OutcomeKinds.CheckpointWritten;

    private static Func<PipelineMessage?, bool> CatchAllPlanner() =>
        msg =>
            msg!.LatestOutcome?.Kind != OutcomeKinds.PlannerProceed
            && msg!.LatestOutcome?.Kind != OutcomeKinds.PlannerProceedWithConstraints
            && msg!.LatestOutcome?.Kind != OutcomeKinds.PlannerNeedsHuman
            && msg!.LatestOutcome?.Kind != OutcomeKinds.PlannerStop;

    private static Func<PipelineMessage?, bool> CatchAllCandidate() =>
        msg => msg!.LatestOutcome?.Kind != OutcomeKinds.CandidateCaptured;

    private static Func<PipelineMessage?, bool> CatchAllVerify() =>
        msg =>
            msg!.LatestOutcome?.Kind != OutcomeKinds.CommandPassed
            && msg!.LatestOutcome?.Kind != OutcomeKinds.CommandFailed;

    private static Func<PipelineMessage?, bool> CatchAllReviewer() =>
        msg =>
            msg!.LatestOutcome?.Kind != OutcomeKinds.ReviewAccepted
            && msg!.LatestOutcome?.Kind != OutcomeKinds.ReviewChangesRequested
            && msg!.LatestOutcome?.Kind != OutcomeKinds.ReviewNeedsHuman;

    private static Func<PipelineMessage?, bool> CatchAllApplyAnswer() =>
        msg =>
            msg!.LatestOutcome?.Payload.TryGetProperty("sourceBlockId", out var sb) != true
            || sb.ValueKind != System.Text.Json.JsonValueKind.String
            || (sb.GetString() != BlockIds.Planner && sb.GetString() != BlockIds.Reviewer);

    private static bool HasVerificationCommands(PipelineMessage msg) =>
        msg.Context.Packet.Verification.Count > 0;

    private static bool NoVerificationCommands(PipelineMessage msg) =>
        msg.Context.Packet.Verification.Count == 0;

    private static bool HasRemainingCommands(PipelineMessage msg) =>
        msg.Context.VerificationIndex < msg.Context.Packet.Verification.Count;

    private static bool AllCommandsComplete(PipelineMessage msg) =>
        msg.Context.VerificationIndex >= msg.Context.Packet.Verification.Count;

    private const string ExecutorInstructions = """
        You are Tandem's implementation block.

        Inspect the workspace and work toward the packet outcomes. When mutation
        authority is closed, use read-only tools to understand the repository and call
        ask_planner with your proposed approach before editing. When authority is open,
        implement the approved approach and constraints.

        Call ask_planner whenever independent guidance is required. During a
        checkpoint-only invocation, call write_checkpoint with the supplied work state.
        When the implementation is ready for verification, call submit_report with
        outcome claims and repository evidence.

        An accepted lifecycle call ends the current turn. Do not represent planner,
        verification, or reviewer decisions yourself.
        """;

    private const string PlannerInstructions = """
        You are Tandem's planner block.

        Review the packet outcomes and constraints, the executor's question,
        proposed approach, and evidence. Read files as needed. Return a structured
        decision: Proceed, ProceedWithConstraints, NeedsHuman, or Stop. Include a
        rationale, any new constraints, and the evidence you used. HumanQuestion must
        be present only for NeedsHuman and null otherwise.
        """;

    private const string ReviewerInstructions = """
        You are Tandem's reviewer block.

        Evaluate the candidate diff against the packet outcomes, planner constraints,
        implementation report, and verification results. You may inspect changed files
        through your read-only tools. Return a structured decision: Accept,
        RequestChanges, or NeedsHuman. Include findings with severity, description,
        and evidence for each issue found. Severity is Critical, High, Medium, or Low.
        HumanQuestion must be present only for NeedsHuman and null otherwise.
        """;
}
