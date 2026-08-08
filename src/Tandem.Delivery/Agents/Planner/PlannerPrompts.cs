using System.Text.Json;

namespace Tandem.Delivery;

public static class PlannerPrompts
{
    public static string BuildMessage(DeliveryState state)
    {
        var packet = state.Packet;
        var outcomes = string.Join(
            "\n",
            packet.Outcomes.Select(o => $"- [{o.Id}] {o.Description}")
        );
        var constraints =
            packet.Constraints.Count > 0
                ? string.Join("\n", packet.Constraints.Select(c => $"- {c}"))
                : "(none)";
        var request = state.ExecutorTransition is ExecutorTransition.PlannerRequested fact
            ? $"Question: {fact.Request.Question}\n"
                + $"Proposed approach: {fact.Request.ProposedApproach}\n"
                + $"Evidence:\n{string.Join("\n", fact.Request.Evidence.Select(item => $"- {item}"))}"
            : "(no request provided)";
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

            Human answer:
            {state.PlannerHumanAnswer ?? "(none)"}

            Return a structured JSON decision: Proceed, ProceedWithConstraints, NeedsHuman, or Stop.
            Example shape (use facts from this workspace, not these values):
            {example}
            """;
    }

    internal const string Instructions = """
        You are Tandem's planner agent.

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
}
