using System.Text.Json;
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
        var nodes = CreateNodes(onUpdate, blockObserver);
        return Compose(nodes);
    }

    /// <summary>
    /// Wires every edge of the SimpleV1 lifecycle in one readable pass. Edges
    /// are grouped by source block. Workflow identity (topology and durable
    /// edge grouping) is pinned by <c>SimpleV1CompositionGraphTests</c>.
    /// </summary>
    private static Workflow Compose(SimpleV1Nodes nodes)
    {
        var builder = new WorkflowBuilder(nodes.Prepare)
            .WithName("simple-v1")
            .WithDescription("Plan, implement, verify, and review a software change.");

        // Prepare
        builder = AddOutcomeEdge(
            builder,
            nodes.Prepare,
            nodes.Executor,
            OutcomeKinds.WorkspacePrepared,
            "workspace prepared"
        );
        builder = builder.AddEdge(
            nodes.Prepare,
            nodes.Failed,
            CatchAllPrepared(),
            label: "unexpected outcome",
            idempotent: false
        );

        // Executor
        builder = AddOutcomeEdge(
            builder,
            nodes.Executor,
            nodes.Planner,
            OutcomeKinds.PlannerRequested,
            "planner requested"
        );
        builder = AddOutcomeEdge(
            builder,
            nodes.Executor,
            nodes.CaptureCandidate,
            OutcomeKinds.ReportSubmitted,
            "report submitted"
        );
        builder = AddOutcomeEdge(
            builder,
            nodes.Executor,
            nodes.Executor,
            OutcomeKinds.CheckpointWritten,
            "checkpoint written"
        );
        builder = builder.AddEdge(
            nodes.Executor,
            nodes.Failed,
            CatchAllExecutor(),
            label: "unexpected outcome",
            idempotent: false
        );

        // Planner
        // Keep successful planner outcomes on one route: the pinned durable
        // adapter batches same-target edges and would deliver String[] where
        // the executor expects one PipelineMessage.
        builder = builder.AddEdge<PipelineMessage<SimpleV1State>>(
            nodes.Planner,
            nodes.Executor,
            msg =>
                msg!.LatestOutcome?.Kind == OutcomeKinds.PlannerProceed
                || msg.LatestOutcome?.Kind == OutcomeKinds.PlannerProceedWithConstraints,
            label: "proceed / proceed with constraints",
            idempotent: false
        );
        builder = AddOutcomeEdge(
            builder,
            nodes.Planner,
            nodes.HumanQuestion,
            OutcomeKinds.PlannerNeedsHuman,
            "needs human"
        );
        builder = AddOutcomeEdge(
            builder,
            nodes.Planner,
            nodes.Failed,
            OutcomeKinds.PlannerStop,
            "stop"
        );
        builder = builder.AddEdge(
            nodes.Planner,
            nodes.Failed,
            CatchAllPlanner(),
            label: "unexpected outcome",
            idempotent: false
        );

        // Capture and verification
        builder = AddOutcomeEdge(
            builder,
            nodes.CaptureCandidate,
            nodes.Verify,
            OutcomeKinds.CandidateCaptured,
            "verification configured",
            HasVerificationCommands
        );
        builder = AddOutcomeEdge(
            builder,
            nodes.CaptureCandidate,
            nodes.Reviewer,
            OutcomeKinds.CandidateCaptured,
            "no verification configured",
            NoVerificationCommands
        );
        builder = builder.AddEdge(
            nodes.CaptureCandidate,
            nodes.Failed,
            CatchAllCandidate(),
            label: "unexpected outcome",
            idempotent: false
        );

        builder = AddOutcomeEdge(
            builder,
            nodes.Verify,
            nodes.Verify,
            OutcomeKinds.CommandPassed,
            "commands remain",
            HasRemainingCommands
        );
        builder = AddOutcomeEdge(
            builder,
            nodes.Verify,
            nodes.Reviewer,
            OutcomeKinds.CommandPassed,
            "verification complete",
            AllCommandsComplete
        );
        builder = AddOutcomeEdge(
            builder,
            nodes.Verify,
            nodes.Executor,
            OutcomeKinds.CommandFailed,
            "command failed"
        );
        builder = builder.AddEdge(
            nodes.Verify,
            nodes.Failed,
            CatchAllVerify(),
            label: "unexpected outcome",
            idempotent: false
        );

        // Review
        builder = AddOutcomeEdge(
            builder,
            nodes.Reviewer,
            nodes.Complete,
            OutcomeKinds.ReviewAccepted,
            "accepted"
        );
        builder = AddOutcomeEdge(
            builder,
            nodes.Reviewer,
            nodes.Executor,
            OutcomeKinds.ReviewChangesRequested,
            "changes requested"
        );
        builder = AddOutcomeEdge(
            builder,
            nodes.Reviewer,
            nodes.HumanQuestion,
            OutcomeKinds.ReviewNeedsHuman,
            "needs human"
        );
        builder = builder.AddEdge(
            nodes.Reviewer,
            nodes.Failed,
            CatchAllReviewer(),
            label: "unexpected outcome",
            idempotent: false
        );

        // Human suspension
        builder = builder.AddEdge(
            nodes.HumanQuestion,
            nodes.HumanInput,
            label: "request human input",
            idempotent: false
        );
        builder = builder.AddEdge(
            nodes.HumanInput,
            nodes.ApplyHumanAnswer,
            label: "answer received",
            idempotent: false
        );

        // Route the answer back to the originating decision block. The
        // apply-human-answer block's outcome payload carries the source
        // block ID; combine same-target routes into one predicate so durable
        // batching cannot deliver String[] where PipelineMessage is expected.
        builder = builder.AddEdge<PipelineMessage<SimpleV1State>>(
            nodes.ApplyHumanAnswer,
            nodes.Planner,
            msg =>
                msg!.LatestOutcome?.Payload.TryGetProperty("sourceBlockId", out var sb) == true
                && sb.ValueKind == System.Text.Json.JsonValueKind.String
                && sb.GetString() == BlockIds.Planner,
            label: "answer for planner",
            idempotent: false
        );
        builder = builder.AddEdge<PipelineMessage<SimpleV1State>>(
            nodes.ApplyHumanAnswer,
            nodes.Reviewer,
            msg =>
                msg!.LatestOutcome?.Payload.TryGetProperty("sourceBlockId", out var sb) == true
                && sb.ValueKind == System.Text.Json.JsonValueKind.String
                && sb.GetString() == BlockIds.Reviewer,
            label: "answer for reviewer",
            idempotent: false
        );
        builder = builder.AddEdge(
            nodes.ApplyHumanAnswer,
            nodes.Failed,
            CatchAllApplyAnswer(),
            label: "unknown answer source",
            idempotent: false
        );

        builder = builder.WithOutputFrom(nodes.Complete, nodes.Failed);
        return builder.Build();
    }

    /// <summary>
    /// Bound executors and the human-input request port for the SimpleV1 lifecycle.
    /// All members are <see cref="ExecutorBinding"/> values so heterogeneous
    /// message types (<see cref="PipelineMessage{SimpleV1State}"/> vs <see cref="HumanQuestion"/>/
    /// <see cref="HumanAnswer"/>) share one typed shape for graph composition.
    /// </summary>
    private sealed record SimpleV1Nodes(
        ExecutorBinding Prepare,
        ExecutorBinding Executor,
        ExecutorBinding Planner,
        ExecutorBinding CaptureCandidate,
        ExecutorBinding Verify,
        ExecutorBinding Reviewer,
        ExecutorBinding Complete,
        ExecutorBinding Failed,
        ExecutorBinding HumanQuestion,
        ExecutorBinding HumanInput,
        ExecutorBinding ApplyHumanAnswer
    );

    /// <summary>
    /// Constructs every block and agent, decorates observed executors, then
    /// binds each to its <see cref="ExecutorBinding"/>. Observer decoration is
    /// applied before binding so the binding wraps the decorated executor and
    /// block events remain observable during runs.
    /// </summary>
    private SimpleV1Nodes CreateNodes(
        Action<string, Guid, AgentResponseUpdate>? onUpdate,
        IBlockExecutionObserver? blockObserver
    )
    {
        Executor<PipelineMessage<SimpleV1State>, PipelineMessage<SimpleV1State>> prepare =
            new PrepareWorkspaceBlock();
        Executor<PipelineMessage<SimpleV1State>, PipelineMessage<SimpleV1State>> executor =
            CreateAgentBlock(
                BlockIds.Executor,
                "implementation",
                ExecutorInstructions,
                ["ask_planner", "submit_report"],
                onUpdate,
                toolInterceptor: CreateMutationGate(),
                turnPolicy: CreateExecutorTurnPolicy()
            );
        Executor<PipelineMessage<SimpleV1State>, PipelineMessage<SimpleV1State>> planner =
            CreateAgentBlock(
                BlockIds.Planner,
                "planning",
                PlannerInstructions,
                [],
                onUpdate,
                PlannerDecisionPolicy.Parse,
                structuredOutputAcceptance: CreatePlannerGroundingPolicy(),
                structuredOutputCorrectionRequiredToolName: "file_access_read",
                configureChatOptions: ConfigurePlannerChatOptions
            );
        Executor<PipelineMessage<SimpleV1State>, PipelineMessage<SimpleV1State>> captureCandidate =
            new CaptureCandidateBlock();
        Executor<PipelineMessage<SimpleV1State>, PipelineMessage<SimpleV1State>> verify =
            new VerificationBlock(blockObserver as ICommandOutputObserver);
        Executor<PipelineMessage<SimpleV1State>, PipelineMessage<SimpleV1State>> reviewer =
            CreateAgentBlock(
                BlockIds.Reviewer,
                "review",
                ReviewerInstructions,
                [],
                onUpdate,
                ReviewDecisionPolicy.Parse,
                messageAugmentation: CreateDiffAugmentation(),
                structuredOutputAcceptance: CreateReviewerGroundingPolicy(),
                structuredOutputCorrectionRequiredToolName: "file_access_read",
                configureChatOptions: ConfigureReviewerChatOptions
            );
        Executor<PipelineMessage<SimpleV1State>, PipelineMessage<SimpleV1State>> complete =
            new CompleteBlock();
        Executor<PipelineMessage<SimpleV1State>, PipelineMessage<SimpleV1State>> failed =
            new FailedBlock();

        // Human-input blocks replace the terminal waiting block.
        Executor<PipelineMessage<SimpleV1State>, HumanQuestion> humanQuestion =
            new HumanQuestionBlock();
        Executor<HumanAnswer, PipelineMessage<SimpleV1State>> applyHumanAnswer =
            new ApplyHumanAnswerBlock();
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

        return new SimpleV1Nodes(
            Prepare: prepare.BindExecutor(),
            Executor: executor.BindExecutor(),
            Planner: planner.BindExecutor(),
            CaptureCandidate: captureCandidate.BindExecutor(),
            Verify: verify.BindExecutor(),
            Reviewer: reviewer.BindExecutor(),
            Complete: complete.BindExecutor(),
            Failed: failed.BindExecutor(),
            HumanQuestion: humanQuestion.BindExecutor(),
            HumanInput: (ExecutorBinding)humanInputPort,
            ApplyHumanAnswer: applyHumanAnswer.BindExecutor()
        );
    }

    private static Executor<TInput, TOutput> Observe<TInput, TOutput>(
        string blockId,
        Executor<TInput, TOutput> executor,
        IBlockExecutionObserver observer
    ) => new ObservedExecutor<TInput, TOutput>(blockId, executor, observer);

    private AgentBlock<SimpleV1State> CreateAgentBlock(
        string blockId,
        string profileName,
        string instructions,
        IReadOnlyList<string> lifecycleTools,
        Action<string, Guid, AgentResponseUpdate>? onUpdate,
        StructuredOutputParser<SimpleV1State>? structuredOutput = null,
        ToolInterceptor<SimpleV1State>? toolInterceptor = null,
        MessageAugmentation<SimpleV1State>? messageAugmentation = null,
        AgentTurnPolicy<SimpleV1State>? turnPolicy = null,
        StructuredOutputAcceptancePolicy<SimpleV1State>? structuredOutputAcceptance = null,
        string? structuredOutputCorrectionRequiredToolName = null,
        Action<ChatOptions>? configureChatOptions = null
    )
    {
        var profile = profileResolver(profileName);
        var checkpoint =
            blockId == BlockIds.Executor
                ? new CheckpointPolicy<SimpleV1State>(
                    profile.ContextWindowTokens,
                    profile.MaxOutputTokens,
                    profile.CheckpointAtPercent,
                    "write_checkpoint",
                    OutcomeKinds.CheckpointWritten,
                    CheckpointOnlyInstructions,
                    BuildCheckpointUserMessage,
                    (state, _, payload) => state with { CheckpointPayload = payload }
                )
                : null;

        var config = new AgentBlockConfig<SimpleV1State>(
            blockId,
            profileName,
            instructions,
            lifecycleTools,
            blockId switch
            {
                BlockIds.Planner => BuildPlannerMessage,
                BlockIds.Reviewer => BuildReviewerMessage,
                _ => BuildExecutorMessage,
            },
            state => state.WorkspacePath,
            state => blockId == BlockIds.Executor && state.MutationAuthorized,
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
                : null
        );
        return new AgentBlock<SimpleV1State>(
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

    private static StructuredOutputAcceptancePolicy<SimpleV1State> CreatePlannerGroundingPolicy() =>
        StructuredOutputAcceptancePolicies.RequireToolCallWhen<SimpleV1State>(
            result =>
                result.Outcome?.Kind
                    is OutcomeKinds.PlannerProceed
                        or OutcomeKinds.PlannerProceedWithConstraints
                || result.Candidate
                    is PlannerDecision
                    {
                        Decision: PlannerDecisionValue.Proceed
                            or PlannerDecisionValue.ProceedWithConstraints,
                    },
            IsRepositoryInspectionTool,
            correction: "Accepted planner decisions require repository inspection in this consult. "
                + "Use an available read-only repository tool to verify the material files and seams, "
                + "then return only the corrected JSON decision with concrete evidenceUsed entries."
        );

    private static StructuredOutputAcceptancePolicy<SimpleV1State> CreateReviewerGroundingPolicy() =>
        StructuredOutputAcceptancePolicies.RequireToolCallWhen<SimpleV1State>(
            result =>
                result.Outcome?.Kind
                    is OutcomeKinds.ReviewAccepted
                        or OutcomeKinds.ReviewChangesRequested
                || result.Candidate
                    is ReviewDecision
                    {
                        Decision: ReviewDecisionValue.Accept or ReviewDecisionValue.RequestChanges,
                    },
            IsRepositoryInspectionTool,
            correction: "Accept and RequestChanges require repository inspection in this review. "
                + "Use an available read-only repository tool to verify the candidate and packet outcomes, "
                + "then return only the corrected JSON decision with concrete outcome evidence."
        );

    private static bool IsRepositoryInspectionTool(string name) =>
        name is "read" or "grep" or "glob"
        || name.StartsWith("file_access_read", StringComparison.Ordinal)
        || name.StartsWith("file_access_search", StringComparison.Ordinal)
        || name.StartsWith("file_access_list", StringComparison.Ordinal)
        || name.StartsWith("gitnexus_", StringComparison.Ordinal)
        || name.Contains("ast_grep", StringComparison.Ordinal);

    private static void ConfigureReviewerChatOptions(ChatOptions options) =>
        options.ResponseFormat = ChatResponseFormat.ForJsonSchema<ReviewDecision>();

    private static ToolInterceptor<SimpleV1State> CreateMutationGate() =>
        (message, fic, ct) =>
        {
            if (message.State.MutationAuthorized)
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

    private static AgentTurnPolicy<SimpleV1State> CreateExecutorTurnPolicy() =>
        new(
            maxContinuationAttempts: 2,
            (observation, _) =>
                ValueTask.FromResult<AgentTurnDirective?>(
                    !observation.Message.State.MutationAuthorized
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

    private static MessageAugmentation<SimpleV1State> CreateDiffAugmentation() =>
        async (message, ct) =>
        {
            var ctx = message.State;
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
        string label,
        Func<PipelineMessage<SimpleV1State>, bool>? extraCondition = null
    )
    {
        return builder.AddEdge<PipelineMessage<SimpleV1State>>(
            source,
            target,
            msg => msg!.LatestOutcome?.Kind == outcomeKind && (extraCondition?.Invoke(msg) ?? true),
            label: label,
            idempotent: false
        );
    }

    private static Func<PipelineMessage<SimpleV1State>?, bool> CatchAllPrepared() =>
        msg => msg!.LatestOutcome?.Kind != OutcomeKinds.WorkspacePrepared;

    private static Func<PipelineMessage<SimpleV1State>?, bool> CatchAllExecutor() =>
        msg =>
            msg!.LatestOutcome?.Kind != OutcomeKinds.PlannerRequested
            && msg!.LatestOutcome?.Kind != OutcomeKinds.ReportSubmitted
            && msg!.LatestOutcome?.Kind != OutcomeKinds.CheckpointWritten;

    private static Func<PipelineMessage<SimpleV1State>?, bool> CatchAllPlanner() =>
        msg =>
            msg!.LatestOutcome?.Kind != OutcomeKinds.PlannerProceed
            && msg!.LatestOutcome?.Kind != OutcomeKinds.PlannerProceedWithConstraints
            && msg!.LatestOutcome?.Kind != OutcomeKinds.PlannerNeedsHuman
            && msg!.LatestOutcome?.Kind != OutcomeKinds.PlannerStop;

    private static Func<PipelineMessage<SimpleV1State>?, bool> CatchAllCandidate() =>
        msg => msg!.LatestOutcome?.Kind != OutcomeKinds.CandidateCaptured;

    private static Func<PipelineMessage<SimpleV1State>?, bool> CatchAllVerify() =>
        msg =>
            msg!.LatestOutcome?.Kind != OutcomeKinds.CommandPassed
            && msg!.LatestOutcome?.Kind != OutcomeKinds.CommandFailed;

    private static Func<PipelineMessage<SimpleV1State>?, bool> CatchAllReviewer() =>
        msg =>
            msg!.LatestOutcome?.Kind != OutcomeKinds.ReviewAccepted
            && msg!.LatestOutcome?.Kind != OutcomeKinds.ReviewChangesRequested
            && msg!.LatestOutcome?.Kind != OutcomeKinds.ReviewNeedsHuman;

    private static Func<PipelineMessage<SimpleV1State>?, bool> CatchAllApplyAnswer() =>
        msg =>
            msg!.LatestOutcome?.Payload.TryGetProperty("sourceBlockId", out var sb) != true
            || sb.ValueKind != System.Text.Json.JsonValueKind.String
            || (sb.GetString() != BlockIds.Planner && sb.GetString() != BlockIds.Reviewer);

    private static bool HasVerificationCommands(PipelineMessage<SimpleV1State> msg) =>
        msg.State.Packet.Verification.Count > 0;

    private static bool NoVerificationCommands(PipelineMessage<SimpleV1State> msg) =>
        msg.State.Packet.Verification.Count == 0;

    private static bool HasRemainingCommands(PipelineMessage<SimpleV1State> msg) =>
        msg.State.VerificationIndex < msg.State.Packet.Verification.Count;

    private static bool AllCommandsComplete(PipelineMessage<SimpleV1State> msg) =>
        msg.State.VerificationIndex >= msg.State.Packet.Verification.Count;

    private static string BuildExecutorMessage(PipelineMessage<SimpleV1State> message)
    {
        var state = message.State;
        var outcomes = string.Join(
            "\n",
            state.Packet.Outcomes.Select(o => $"- [{o.Id}] {o.Description}")
        );
        var constraints =
            state.Packet.Constraints.Count == 0
                ? "(none)"
                : string.Join("\n", state.Packet.Constraints.Select(c => $"- {c}"));
        var planner = state.PlannerDecision is { } decision
            ? $"""

                Planner decision: {decision.Decision}
                Planner rationale: {decision.Rationale}
                Planner constraints:
                {string.Join(
                    "\n",
                    decision.Constraints.Count > 0
                        ? decision.Constraints.Select(c => $"- {c}")
                        : ["(none)"]
                )}
                """
            : "";
        var verification =
            state.VerificationResults.Count > 0
                ? $"\nLatest verification failure (if any):\n{FormatVerificationResults(state.VerificationResults)}"
                : "";
        var candidate = state.CandidateSha is { } sha ? $"\nCurrent candidate SHA: {sha}" : "";
        var checkpoint = state.CheckpointPayload is { } payload
            ? $"\nPrevious checkpoint (context was compacted, continue from here):\n{payload.GetRawText()}"
            : "";
        return $"""
            Packet: {state.Packet.Title}
            Workspace: {state.WorkspacePath}
            Pinned base: {state.PinnedBaseSha}
            Mutation authorized: {state.MutationAuthorized}

            Outcomes:
            {outcomes}

            Constraints:
            {constraints}{planner}{verification}{candidate}{checkpoint}
            """;
    }

    private static string BuildPlannerMessage(PipelineMessage<SimpleV1State> message)
    {
        var state = message.State;
        var packet = state.Packet;
        var outcomes = string.Join(
            "\n",
            packet.Outcomes.Select(o => $"- [{o.Id}] {o.Description}")
        );
        var constraints =
            packet.Constraints.Count > 0
                ? string.Join("\n", packet.Constraints.Select(c => $"- {c}"))
                : "(none)";
        var request = message.LatestOutcome?.Payload.GetRawText() ?? "(no request provided)";
        var previous =
            state.PlannerConstraints.Count > 0
                ? string.Join("\n", state.PlannerConstraints.Select(c => $"- {c}"))
                : "(none)";
        var example = JsonSerializer.Serialize(
            new
            {
                decision = "Proceed",
                rationale = "The inspected implementation seams support the proposed approach.",
                constraints = Array.Empty<string>(),
                evidenceUsed = new[] { "src/example.ts: inspected implementation seam." },
                humanQuestion = (string?)null,
            },
            new JsonSerializerOptions { WriteIndented = true }
        );
        return $"""
            Packet: {packet.Title}
            Workspace: {state.WorkspacePath}

            Outcomes:
            {outcomes}

            Constraints:
            {constraints}

            Executor request:
            {request}

            Previous planner constraints:
            {previous}

            Return a structured JSON decision: Proceed, ProceedWithConstraints, NeedsHuman, or Stop.
            Example shape (use facts from this workspace, not these values):
            {example}
            """;
    }

    internal static string BuildReviewerMessage(PipelineMessage<SimpleV1State> message)
    {
        var state = message.State;
        var outcomes = string.Join(
            "\n",
            state.Packet.Outcomes.Select(o => $"- [{o.Id}] {o.Description}")
        );
        var plannerConstraints =
            state.PlannerConstraints.Count > 0
                ? string.Join("\n", state.PlannerConstraints.Select(c => $"- {c}"))
                : "(none)";
        var verification =
            state.VerificationResults.Count > 0
                ? FormatVerificationResults(state.VerificationResults)
                : "(no verification commands)";
        var example = JsonSerializer.Serialize(
            new
            {
                decision = "Accept",
                summary = "Every packet outcome is implemented and supported by evidence.",
                outcomes = state.Packet.Outcomes.Select(outcome => new
                {
                    outcomeId = outcome.Id,
                    delivered = true,
                    evidence = new[] { $"src/example.ts: '{outcome.Description}'." },
                }),
                findings = Array.Empty<object>(),
                humanQuestion = (string?)null,
            },
            new JsonSerializerOptions { WriteIndented = true }
        );
        return $"""
            Packet: {state.Packet.Title}
            Workspace: {state.WorkspacePath}
            Pinned base: {state.PinnedBaseSha}
            Candidate SHA: {state.CandidateSha ?? "(no candidate)"}

            Outcomes:
            {outcomes}

            Planner constraints:
            {plannerConstraints}

            Verification results:
            {verification}

            Implementation report:
            {state.ImplementationReport?.GetRawText() ?? "(none)"}

            Human answer for this review:
            {state.ReviewerHumanAnswer ?? "(none)"}

            You may inspect changed files through your read-only tools.
            Return a structured JSON decision: Accept, RequestChanges, or NeedsHuman.
            Assess every outcome ID exactly once. Example shape:
            {example}
            """;
    }

    private static string FormatVerificationResults(IReadOnlyList<VerificationResult> results) =>
        string.Join(
            "\n",
            results.Select(result =>
                $"[{(result.ExitCode == 0 ? "PASS" : "FAIL")}] {result.Command} "
                + $"(exit {result.ExitCode})\nstdout: {result.Stdout}\nstderr: {result.Stderr}"
            )
        );

    private static string BuildCheckpointUserMessage(PipelineMessage<SimpleV1State> message)
    {
        var usage = message.Runtime.AgentUsage.GetValueOrDefault(BlockIds.Executor);
        return $"""
            Context window approaching limit: {usage?.CurrentContextTokens ?? 0} tokens used.
            Write a checkpoint of your current work state using the write_checkpoint tool.
            Call write_checkpoint now.
            """;
    }

    private const string CheckpointOnlyInstructions = """
        You are Tandem's implementation block in checkpoint-only mode.

        Your context window is approaching its limit. You must write a checkpoint
        of your current work state using the write_checkpoint tool. Summarize
        what you have completed and what remains to be done next.

        This is the only action available. Do not attempt other work.
        """;

    private const string ExecutorInstructions = """
        You are Tandem's implementation block.

        You implement; you do not make planner, reviewer, verification, or human
        decisions. Inspect the workspace and work toward the packet outcomes. Read files
        before editing them and treat the repository as the source of truth for code facts.

        When mutation authority is closed, use your read-only tools to understand the
        relevant repository seams, then call ask_planner with your proposed approach and
        the evidence you inspected. Do not ask the planner to read a specific local fact
        that you can inspect yourself. When authority is open, implement the approved
        approach and satisfy every planner constraint.

        Call ask_planner when engineering direction, scope interpretation, architecture,
        repository procedure, or a changed plan requires independent guidance. Questions
        about product, UX, business policy, security policy, permissions, tenancy, data,
        migration, legal, or compliance belong to the human and must be routed through the
        planner rather than answered or guessed by you.

        During a checkpoint-only invocation, call write_checkpoint with the supplied work
        state. When the implementation is ready for verification, call submit_report with
        outcome claims and concrete repository evidence. Do not claim that work is complete
        merely because code was written; the configured verification and review stages own
        that decision.

        An accepted lifecycle call ends the current turn. Do not represent planner,
        verification, reviewer, or human decisions in prose. Use the lifecycle tools.
        """;

    private const string PlannerInstructions = """
        You are Tandem's planner block.

        You decide engineering direction; you do not implement. Review the packet outcomes
        and constraints, the executor's question, proposed approach, and evidence. Treat
        the executor's evidence as pointers to verify, not proof.

        You have read-only access to the entire workspace. When a decision depends on a
        repository fact, inspect it yourself before deciding and cite the files, symbols,
        or other facts you inspected. Do not ask the executor or human to provide source
        files, signatures, configuration, tests, diffs, or any other repository evidence
        available through your tools. Failure to inspect available evidence is not a reason
        to escalate.

        Return one structured decision:

        - Proceed when the evidence is sufficient and the engineering direction is clear.
        - ProceedWithConstraints when the approach is sound but concrete, checkable
          implementation obligations remain.
        - NeedsHuman only when the missing decision belongs to the human: product, UX,
          business policy, security policy, permissions, tenancy, data policy, migration
          policy, legal, or compliance. Repository facts and engineering decisions are not
          human questions.
        - Stop only when you cannot state a safe engineering next action after inspecting
          the packet, supplied context, and available repository evidence. Do not use Stop
          merely because inspection has not yet been performed.

        Audit the executor's proposed approach, not only its literal question. Correct a
        false premise, incomplete surface, or approach that would break existing behavior.
        Include a direct rationale and the evidence you actually used. Proceed means no
        implementation obligations remain and Constraints must be empty.
        ProceedWithConstraints means concrete, checkable obligations remain and Constraints
        must contain every such obligation.

        Return exactly one JSON object matching the required response schema. Do not add
        reasoning, narration, apologies, markdown fences, or text before or after the JSON.
        HumanQuestion must be present only for NeedsHuman and null otherwise.
        """;

    private const string ReviewerInstructions = """
        You are Tandem's reviewer block.

        You independently judge whether the verified candidate delivers the packet outcomes
        and remains sound. Treat the implementation report and prior approval as claims to
        check, not proof. Evaluate the exact candidate diff, packet outcomes, planner
        constraints, implementation report, verification results, and relevant existing
        behavior.

        You have read-only access to the entire candidate workspace, not only the injected
        diff. Inspect any changed or unchanged source, tests, contracts, or configuration
        needed to judge the candidate. Do not ask the executor or human to provide
        repository contents or evidence available through your tools. Failure to inspect
        available evidence is not a reason to escalate.

        Return one structured decision:

        - Accept when the outcomes are delivered, the verified diff is sound, and no
          material issue remains.
        - RequestChanges for a concrete correctness, integration, scope, safety, or test
          quality problem the executor can fix. Make every finding specific and actionable.
        - NeedsHuman only for a decision owned by the human: product, UX, business policy,
          security policy, permissions, tenancy, data policy, migration policy, legal, or
          compliance. Missing repository inspection is not NeedsHuman.

        Ground every finding in evidence. Include its severity, a precise description, and
        the file, symbol, behavior, verification result, or contract that proves it.
        Severity is Critical, High, Medium, or Low. Do not manufacture findings to justify
        another pass, and do not block on pure taste.

        Return exactly one JSON object matching the required response schema. Do not add
        reasoning, narration, apologies, markdown fences, or text before or after the JSON.
        HumanQuestion must be present only for NeedsHuman and null otherwise.
        """;
}
