using System.Text.Json;
using Tandem.Advanced;

namespace Tandem.Delivery;

public static class DeliveryPrompts
{
    public static string BuildExecutorMessage(DeliveryState state)
    {
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

    public static string BuildPlannerMessage(AgentMessageContext<DeliveryState> context)
    {
        var state = context.State;
        var packet = state.Packet;
        var outcomes = string.Join(
            "\n",
            packet.Outcomes.Select(o => $"- [{o.Id}] {o.Description}")
        );
        var constraints =
            packet.Constraints.Count > 0
                ? string.Join("\n", packet.Constraints.Select(c => $"- {c}"))
                : "(none)";
        var request = context.LatestOutcome?.Payload.GetRawText() ?? "(no request provided)";
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

    public static string BuildReviewerMessage(DeliveryState state)
    {
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

    internal static string BuildCheckpointUserMessage(
        AgentCheckpointContext<DeliveryState> context
    ) =>
        $"""
            Context window approaching limit: {context.CurrentContextTokens} tokens used.
            Write a checkpoint of your current work state using the write_checkpoint tool.
            Call write_checkpoint now.
            """;

    private static string FormatVerificationResults(IReadOnlyList<VerificationResult> results) =>
        string.Join(
            "\n",
            results.Select(result =>
                $"[{(result.ExitCode == 0 ? "PASS" : "FAIL")}] {result.Command} "
                + $"(exit {result.ExitCode})\nstdout: {result.Stdout}\nstderr: {result.Stderr}"
            )
        );

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
