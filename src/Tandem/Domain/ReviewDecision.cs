namespace Tandem.Domain;

public sealed record ReviewDecision(
    ReviewDecisionValue Decision,
    string Summary,
    IReadOnlyList<ReviewFinding> Findings,
    string? HumanQuestion = null
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
