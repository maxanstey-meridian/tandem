namespace Tandem.Delivery;

public sealed record PlannerDecision(
    PlannerDecisionValue Decision,
    string Rationale,
    IReadOnlyList<string> Constraints,
    IReadOnlyList<string> EvidenceUsed,
    string? HumanQuestion = null
);

public enum PlannerDecisionValue
{
    Proceed,
    ProceedWithConstraints,
    NeedsHuman,
    Stop,
}
