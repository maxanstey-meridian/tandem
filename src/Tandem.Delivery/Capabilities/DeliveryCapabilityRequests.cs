namespace Tandem.Delivery;

public sealed record AskPlannerRequest(
    string Question,
    string ProposedApproach,
    IReadOnlyList<string> Evidence
);

public sealed record SubmitReportRequest(
    string Summary,
    IReadOnlyList<string> Outcomes,
    IReadOnlyList<string> Evidence
);

public sealed record WriteCheckpointRequest(
    string Summary,
    IReadOnlyList<string> Completed,
    IReadOnlyList<string> InspectedFiles,
    IReadOnlyList<string> Uncertainties,
    string NextAction
);
