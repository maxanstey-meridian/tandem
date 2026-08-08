namespace Tandem.Delivery;

public sealed record ReviewDecision(
    ReviewDecisionValue Decision,
    string Summary,
    IReadOnlyList<ReviewOutcomeAssessment> Outcomes,
    IReadOnlyList<ReviewFinding> Findings,
    string? HumanQuestion = null
);

public sealed record ReviewOutcomeAssessment(
    string OutcomeId,
    bool Delivered,
    IReadOnlyList<string> Evidence
);

public enum ReviewDecisionValue
{
    Accept,
    RequestChanges,
    NeedsHuman,
}

public sealed record ReviewFinding(
    ReviewFindingSeverity Severity,
    string Description,
    string Evidence
);

public enum ReviewFindingSeverity
{
    Critical,
    High,
    Medium,
    Low,
}
