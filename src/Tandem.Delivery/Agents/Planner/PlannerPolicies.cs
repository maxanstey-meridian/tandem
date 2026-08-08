using Tandem.Advanced;

namespace Tandem.Delivery;

public static class PlannerPolicies
{
    public static OutputAcceptancePolicy<DeliveryState, PlannerDecision> RepositoryGrounded() =>
        observation =>
            observation.Output.Decision
                is not (PlannerDecisionValue.Proceed or PlannerDecisionValue.ProceedWithConstraints)
            || observation.Tools.Any(tool => tool.Evidence == ToolEvidence.RepositoryInspection)
                ? []
                :
                [
                    new StructuredOutputProblem(
                        "$grounding",
                        "Accepted planner decisions require repository inspection in this consult. "
                            + "Use an available read-only repository tool to verify the material files and seams, "
                            + "then return only the corrected JSON decision with concrete evidenceUsed entries."
                    ),
                ];
}
