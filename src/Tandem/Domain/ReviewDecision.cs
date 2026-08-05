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

public sealed record ReviewFinding(string Severity, string Description, string Evidence);
