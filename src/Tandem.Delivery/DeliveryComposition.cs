using System.Text.Json;
using Microsoft.Extensions.AI;
using Tandem.Domain;
using Tandem.Infrastructure.Blocks;

namespace Tandem.Infrastructure.Composition;

public sealed class DeliveryComposition(DeliveryStepsFactory stepsFactory)
{
    public Pipeline Build(PipelineBuildContext context)
    {
        var delivery = stepsFactory.Create(context);
        return TandemWorkflow
            .Start(
                at: delivery.PrepareWorkspace,
                name: "delivery",
                description: "Plan, implement, verify, and review a software change."
            )
            .Route(
                on: delivery.PrepareWorkspace.Result.Prepared,
                to: delivery.Executor,
                label: "workspace prepared"
            )
            .Route(
                on: delivery.PrepareWorkspace.Result.Unexpected,
                to: delivery.FailRun,
                label: "unexpected outcome"
            )
            .Route(
                on: delivery.Executor.Result.PlannerRequested,
                to: delivery.Planner,
                label: "planner requested"
            )
            .Route(
                on: delivery.Executor.Result.ReportSubmitted,
                to: delivery.CaptureCandidate,
                label: "report submitted"
            )
            .Route(
                on: delivery.Executor.Result.CheckpointWritten,
                to: delivery.Executor,
                label: "checkpoint written"
            )
            .Route(
                on: delivery.Executor.Result.Unexpected,
                to: delivery.FailRun,
                label: "unexpected outcome"
            )
            .Route(
                on: delivery.Planner.Result.Proceed,
                to: delivery.Executor,
                label: "proceed / proceed with constraints"
            )
            .Route(
                on: delivery.Planner.Result.NeedsHuman,
                to: delivery.HumanQuestion,
                label: "needs human"
            )
            .Route(on: delivery.Planner.Result.Stop, to: delivery.FailRun, label: "stop")
            .Route(
                on: delivery.Planner.Result.Unexpected,
                to: delivery.FailRun,
                label: "unexpected outcome"
            )
            .Route(
                on: delivery.CaptureCandidate.Result.Captured,
                when: HasVerificationCommands,
                to: delivery.Verification,
                label: "verification configured"
            )
            .Route(
                on: delivery.CaptureCandidate.Result.Captured,
                when: NoVerificationCommands,
                to: delivery.Reviewer,
                label: "no verification configured"
            )
            .Route(
                on: delivery.CaptureCandidate.Result.Unexpected,
                to: delivery.FailRun,
                label: "unexpected outcome"
            )
            .Route(
                on: delivery.Verification.Result.Passed,
                when: HasRemainingCommands,
                to: delivery.Verification,
                label: "commands remain"
            )
            .Route(
                on: delivery.Verification.Result.Passed,
                when: AllCommandsComplete,
                to: delivery.Reviewer,
                label: "verification complete"
            )
            .Route(
                on: delivery.Verification.Result.Failed,
                to: delivery.Executor,
                label: "command failed"
            )
            .Route(
                on: delivery.Verification.Result.Unexpected,
                to: delivery.FailRun,
                label: "unexpected outcome"
            )
            .Route(
                on: delivery.Reviewer.Result.Accepted,
                to: delivery.CompleteRun,
                label: "accepted"
            )
            .Route(
                on: delivery.Reviewer.Result.ChangesRequested,
                to: delivery.Executor,
                label: "changes requested"
            )
            .Route(
                on: delivery.Reviewer.Result.NeedsHuman,
                to: delivery.HumanQuestion,
                label: "needs human"
            )
            .Route(
                on: delivery.Reviewer.Result.Unexpected,
                to: delivery.FailRun,
                label: "unexpected outcome"
            )
            .Route(
                from: delivery.HumanQuestion,
                to: delivery.HumanInput,
                label: "request human input"
            )
            .Route(
                from: delivery.HumanInput,
                to: delivery.ApplyHumanAnswer,
                label: "answer received"
            )
            .Route(
                when: IsPlannerHumanAnswer,
                from: delivery.ApplyHumanAnswer,
                to: delivery.Planner,
                label: "answer for planner"
            )
            .Route(
                when: IsReviewerHumanAnswer,
                from: delivery.ApplyHumanAnswer,
                to: delivery.Reviewer,
                label: "answer for reviewer"
            )
            .Route(
                when: IsUnknownHumanAnswer,
                from: delivery.ApplyHumanAnswer,
                to: delivery.FailRun,
                label: "unknown answer source"
            )
            .Build(delivery.CompleteRun, delivery.FailRun);
    }

    /// <summary>
    /// Wires every edge of the Delivery lifecycle in one readable pass. Edges
    /// are grouped by source block. Workflow identity (topology and durable
    /// edge grouping) is pinned by <c>DeliveryCompositionGraphTests</c>.
    /// </summary>
    public static void ConfigurePlannerChatOptions(ChatOptions options) =>
        options.ResponseFormat = ChatResponseFormat.ForJsonSchema<PlannerDecision>();

    public static StructuredOutputAcceptancePolicy<DeliveryState> CreatePlannerGroundingPolicy() =>
        StructuredOutputAcceptancePolicies.RequireToolCallWhen<DeliveryState>(
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

    public static StructuredOutputAcceptancePolicy<DeliveryState> CreateReviewerGroundingPolicy() =>
        StructuredOutputAcceptancePolicies.RequireToolCallWhen<DeliveryState>(
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

    public static void ConfigureReviewerChatOptions(ChatOptions options) =>
        options.ResponseFormat = ChatResponseFormat.ForJsonSchema<ReviewDecision>();

    public static ToolInterceptor<DeliveryState> CreateMutationGate() =>
        (message, fic, ct) =>
        {
            if (message.State.MutationAuthorized)
            {
                return ValueTask.FromResult<ToolInterceptionResult?>(null);
            }

            var name = fic.Function.Name;
            var isWrite = IsWorkspaceMutationTool(name);

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

    public static bool IsWorkspaceMutationTool(string name) =>
        name.StartsWith("file_access_write", StringComparison.Ordinal)
        || name.StartsWith("file_access_replace", StringComparison.Ordinal)
        || name.StartsWith("file_access_delete", StringComparison.Ordinal)
        || name.StartsWith("file_access_move", StringComparison.Ordinal)
        || name.StartsWith("file_access_create", StringComparison.Ordinal);

    public static IReadOnlyList<string> LifecycleToolsFor(string blockId) =>
        blockId == BlockIds.Executor ? ["ask_planner", "submit_report"] : [];

    public static bool OwnsCheckpointPolicy(string blockId) => blockId == BlockIds.Executor;

    public static bool AllowsWorkspaceMutation(string blockId, DeliveryState state) =>
        blockId == BlockIds.Executor && state.MutationAuthorized;

    public static AgentTurnPolicy<DeliveryState> CreateExecutorTurnPolicy() =>
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

    public static MessageAugmentation<DeliveryState> CreateDiffAugmentation() =>
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

    private static bool HasVerificationCommands(PipelineMessage<DeliveryState> msg) =>
        msg.State.Packet.Verification.Count > 0;

    private static bool NoVerificationCommands(PipelineMessage<DeliveryState> msg) =>
        msg.State.Packet.Verification.Count == 0;

    private static bool HasRemainingCommands(PipelineMessage<DeliveryState> msg) =>
        msg.State.VerificationIndex < msg.State.Packet.Verification.Count;

    private static bool AllCommandsComplete(PipelineMessage<DeliveryState> msg) =>
        msg.State.VerificationIndex >= msg.State.Packet.Verification.Count;

    private static bool IsPlannerHumanAnswer(PipelineMessage<DeliveryState> message) =>
        HumanAnswerSource(message) == BlockIds.Planner;

    private static bool IsReviewerHumanAnswer(PipelineMessage<DeliveryState> message) =>
        HumanAnswerSource(message) == BlockIds.Reviewer;

    private static bool IsUnknownHumanAnswer(PipelineMessage<DeliveryState> message) =>
        HumanAnswerSource(message) is not (BlockIds.Planner or BlockIds.Reviewer);

    private static string? HumanAnswerSource(PipelineMessage<DeliveryState> message) =>
        message.LatestOutcome?.Payload.TryGetProperty("sourceBlockId", out var source) == true
        && source.ValueKind == JsonValueKind.String
            ? source.GetString()
            : null;

    public static string BuildExecutorMessage(PipelineMessage<DeliveryState> message)
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

    public static string BuildPlannerMessage(PipelineMessage<DeliveryState> message)
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

    public static string BuildReviewerMessage(PipelineMessage<DeliveryState> message)
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

    internal static string BuildCheckpointUserMessage(PipelineMessage<DeliveryState> message)
    {
        var usage = message.Runtime.AgentUsage.GetValueOrDefault(BlockIds.Executor);
        return $"""
            Context window approaching limit: {usage?.CurrentContextTokens ?? 0} tokens used.
            Write a checkpoint of your current work state using the write_checkpoint tool.
            Call write_checkpoint now.
            """;
    }

    internal const string CheckpointOnlyInstructions = """
        You are Tandem's implementation block in checkpoint-only mode.

        Your context window is approaching its limit. You must write a checkpoint
        of your current work state using the write_checkpoint tool. Summarize
        what you have completed and what remains to be done next.

        This is the only action available. Do not attempt other work.
        """;

    internal const string ExecutorInstructions = """
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

    internal const string PlannerInstructions = """
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

    internal const string ReviewerInstructions = """
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
