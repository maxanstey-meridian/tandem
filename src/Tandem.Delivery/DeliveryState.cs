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
    ExecutorAcceptedFact? ExecutorAcceptedFact,
    ReviewDecision? ReviewerDecision,
    string? PlannerHumanAnswer,
    string? ReviewerHumanAnswer,
    RunStatus Status
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
            ExecutorAcceptedFact: null,
            ReviewerDecision: null,
            PlannerHumanAnswer: null,
            ReviewerHumanAnswer: null,
            Status: RunStatus.Running
        );
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ExecutorAcceptedFact.PlannerRequested), "planner-requested")]
[JsonDerivedType(typeof(ExecutorAcceptedFact.ReportSubmitted), "report-submitted")]
[JsonDerivedType(typeof(ExecutorAcceptedFact.CheckpointWritten), "checkpoint-written")]
public abstract record ExecutorAcceptedFact
{
    public sealed record PlannerRequested(AskPlannerRequest Request) : ExecutorAcceptedFact;

    public sealed record ReportSubmitted(SubmitReportRequest Report) : ExecutorAcceptedFact;

    public sealed record CheckpointWritten(WriteCheckpointRequest Checkpoint)
        : ExecutorAcceptedFact;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RunStatus
{
    Running,
    Ready,
    WaitingForHuman,
    Failed,
    Faulted,
    Cancelled,
}
