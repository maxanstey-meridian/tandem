namespace Tandem.Delivery;

public enum DeliveryLedgerRole
{
    Executor,
    Planner,
    Reviewer,
}

public sealed record DeliveryLedgerContext(
    OutcomeProgressDocument? Outcomes,
    AcceptedImplementationReportDocument? Report,
    ProgressCheckpointRecord? LatestCheckpoint,
    IReadOnlyList<PlannerDecisionRecord> PlannerDecisions,
    IReadOnlyList<ReviewDecisionRecord> Reviews,
    IReadOnlyList<VerificationResultRecord> VerificationResults,
    IReadOnlyList<HumanAnswerRecord> HumanAnswers
);
