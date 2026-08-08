using Tandem.Advanced;

namespace Tandem.Delivery;

public static class PlannerPolicies
{
    public static StructuredOutputAcceptancePolicy<DeliveryState> RequireRepositoryGrounding() =>
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
            RepositoryGrounding.IsInspectionTool,
            correction: "Accepted planner decisions require repository inspection in this consult. "
                + "Use an available read-only repository tool to verify the material files and seams, "
                + "then return only the corrected JSON decision with concrete evidenceUsed entries."
        );
}
