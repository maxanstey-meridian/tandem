using Tandem.Advanced;

namespace Tandem.Delivery;

public static class ExecutorPrompts
{
    public static string BuildMessage(DeliveryState state)
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
                ? $"\nLatest verification failure (if any):\n{VerificationResultFormatting.Format(state.VerificationResults)}"
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

    internal static string BuildCheckpointMessage(AgentCheckpointContext<DeliveryState> context) =>
        $"""
            Context window approaching limit: {context.CurrentContextTokens} tokens used.
            Write a checkpoint of your current work state using the write_checkpoint tool.
            Call write_checkpoint now.
            """;

    internal const string CheckpointInstructions = """
        You are Tandem's implementation block in checkpoint-only mode.

        Your context window is approaching its limit. You must write a checkpoint
        of your current work state using the write_checkpoint tool. Summarize
        what you have completed and what remains to be done next.

        This is the only action available. Do not attempt other work.
        """;

    internal const string Instructions = """
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
}
