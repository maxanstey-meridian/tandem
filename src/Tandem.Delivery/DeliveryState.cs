using System.Text.Json.Serialization;

namespace Tandem.Delivery;

public sealed record DeliveryState(
    Packet Packet,
    string PinnedBaseSha,
    string WorkspacePath,
    bool MutationAuthorized,
    PlannerDecision? PlannerDecision,
    IReadOnlyList<string> PlannerConstraints,
    string? CandidateSha,
    int VerificationIndex,
    IReadOnlyList<VerificationResult> VerificationResults,
    ExecutorTransition? ExecutorTransition,
    ReviewDecision? ReviewerDecision,
    string? PlannerHumanAnswer,
    string? ReviewerHumanAnswer
)
{
    public static DeliveryState Create(Packet packet, string pinnedBaseSha, string workspacePath) =>
        new(
            packet,
            pinnedBaseSha,
            workspacePath,
            MutationAuthorized: false,
            PlannerDecision: null,
            PlannerConstraints: [],
            CandidateSha: null,
            VerificationIndex: 0,
            VerificationResults: [],
            ExecutorTransition: null,
            ReviewerDecision: null,
            PlannerHumanAnswer: null,
            ReviewerHumanAnswer: null
        );

    public DeliveryState RecordPlannerDecision(PlannerDecision decision)
    {
        var authorizesMutation =
            decision.Decision
            is PlannerDecisionValue.Proceed
                or PlannerDecisionValue.ProceedWithConstraints;
        return this with
        {
            PlannerDecision = decision,
            PlannerConstraints =
                decision.Constraints.Count > 0 ? decision.Constraints : PlannerConstraints,
            MutationAuthorized = authorizesMutation || MutationAuthorized,
            PlannerHumanAnswer = null,
        };
    }

    public DeliveryState RecordReviewDecision(ReviewDecision decision) =>
        this with
        {
            ReviewerDecision = decision,
            ReviewerHumanAnswer = null,
        };

    public DeliveryState RecordPlannerRequest(AskPlannerRequest request) =>
        this with
        {
            ExecutorTransition = new ExecutorTransition.PlannerRequested(request),
        };

    public DeliveryState RecordImplementationReport(SubmitReportRequest request) =>
        this with
        {
            ExecutorTransition = new ExecutorTransition.ReportSubmitted(request),
        };

    public DeliveryState RecordCheckpoint(WriteCheckpointRequest request) =>
        this with
        {
            ExecutorTransition = new ExecutorTransition.CheckpointWritten(request),
        };
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ExecutorTransition.PlannerRequested), "planner-requested")]
[JsonDerivedType(typeof(ExecutorTransition.ReportSubmitted), "report-submitted")]
[JsonDerivedType(typeof(ExecutorTransition.CheckpointWritten), "checkpoint-written")]
public abstract record ExecutorTransition
{
    public sealed record PlannerRequested(AskPlannerRequest Request) : ExecutorTransition;

    public sealed record ReportSubmitted(SubmitReportRequest Report) : ExecutorTransition;

    public sealed record CheckpointWritten(WriteCheckpointRequest Checkpoint) : ExecutorTransition;
}
