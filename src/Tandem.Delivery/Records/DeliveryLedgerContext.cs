namespace Tandem.Delivery;

public enum DeliveryLedgerRole
{
    Executor,
    Planner,
    Reviewer,
}

public sealed record DeliveryLedgerContext(
    OutcomeProgressDocument? Outcomes,
    SubmitReportRequest? Report,
    ProgressCheckpointRecord? LatestCheckpoint,
    IReadOnlyList<PlannerDecision> PlannerDecisions,
    IReadOnlyList<ReviewDecision> Reviews,
    IReadOnlyList<VerificationResult> VerificationResults,
    IReadOnlyList<HumanAnswerRecord> HumanAnswers
);
