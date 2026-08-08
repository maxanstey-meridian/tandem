using System.Text.Json;

namespace Tandem.Delivery;

public static class ReviewerPrompts
{
    public static string BuildMessage(DeliveryState state)
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
                ? VerificationResultFormatting.Format(state.VerificationResults)
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

    internal const string Instructions = """
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
